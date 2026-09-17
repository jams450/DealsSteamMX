using System.Collections.Concurrent;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.GgDeals;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Offer cache window. Optional in the constructor: if the host does not register it, the
/// default (7 days) applies, so the service never fails to resolve.
/// </summary>
public sealed record SteamOffersSettings(int RefreshAfterDays);

public sealed class SteamGameService(
    IRepository repository,
    ISteamStoreClient steamClient,
    IItadClient itadClient,
    IGgDealsClient ggDealsClient,
    IFxRateService fxRateService,
    SteamOffersSettings? offersSettings = null) : ISteamGameService
{
    private const string Region = "mx";
    private const string GameType = "game";
    private const string ItadSource = "itad";
    private const string GgDealsSource = "ggdeals";
    private const string GgDealsRetailOfferKey = "retail";
    private const string GgDealsKeyshopOfferKey = "keyshop";
    private const string GgDealsRetailShopName = "GG.deals";
    private const string GgDealsKeyshopShopName = "GG.deals keyshops";
    private const string OfficialClassification = "official";
    private const string AuthorizedClassification = "authorized";
    private const string KeyshopClassification = "keyshop";
    private const string RegionalPricing = "regional";
    private const string FxEstimatePricing = "fx_estimate";
    private const string UnconvertedPricing = "unconverted";
    private const string MxnCurrency = "MXN";
    private const string FxBaseCurrency = "USD";
    private const string FxQuoteCurrency = "MXN";
    private const int DefaultRefreshAfterDays = 7;
    private const int MinRefreshAfterDays = 1;
    private const int MaxRefreshAfterDays = 90;
    private const int MinSuggestionLength = 2;
    private const int MaxSuggestionLength = 100;
    private const int SuggestionLimit = 10;

    // ponytail: one gate per app id, kept for the process lifetime and bounded by the distinct app ids
    // requested; serializes the same-app DB+HTTP transaction so concurrent requests cannot race the
    // unique (app, region) insert or the offer keys. No idle eviction needed at this scale.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> AppGates = new();

    public async Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var results = await steamClient.SearchAsync(normalized, cancellationToken);
        var observedAt = DateTime.UtcNow;

        foreach (var result in results)
        {
            var game = await repository.GetTrack<SteamGame>()
                .FirstOrDefaultAsync(g => g.AppId == result.AppId && g.Region == Region, cancellationToken);
            if (game == null)
            {
                await repository.Save(new SteamGame
                {
                    AppId = result.AppId,
                    Name = result.Name,
                    Type = result.Type,
                    ImageUrl = result.ImageUrl,
                    Region = Region,
                    ObservedAt = observedAt
                });
                continue;
            }

            game.Name = result.Name;
            game.Type = result.Type;
            // Search artwork is the small tiny_image; never overwrite richer detail artwork already stored.
            if (string.IsNullOrWhiteSpace(game.ImageUrl) && !string.IsNullOrWhiteSpace(result.ImageUrl))
            {
                game.ImageUrl = result.ImageUrl;
            }

            game.ObservedAt = observedAt;
            await repository.SaveChangesAsync();
        }

        return results;
    }

    public async Task<IReadOnlyList<SteamSearchResult>> GetSuggestionsAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalized = query.Trim();
        if (normalized.Length > MaxSuggestionLength)
        {
            normalized = normalized[..MaxSuggestionLength];
        }

        if (normalized.Length < MinSuggestionLength)
        {
            return [];
        }

        // ToLower + Contains translates to lower()/strpos in Npgsql: case-insensitive and literal,
        // so user wildcards like % or _ are matched as text, not as LIKE patterns.
        var needle = normalized.ToLower();
        return await repository.Get<SteamGame>()
            .Where(g => g.Region == Region && g.Name.ToLower().Contains(needle))
            .OrderBy(g => g.Name)
            .Take(SuggestionLimit)
            .Select(g => new SteamSearchResult(g.AppId, g.Name, g.Type, g.ImageUrl))
            .ToListAsync(cancellationToken);
    }

    public async Task<SteamGameDetails?> GetByAppIdAsync(int appId, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            throw new ArgumentException("AppID must be greater than zero.", nameof(appId));
        }

        // The gate is acquired before any DB or HTTP work and released on every path (success, early
        // return, provider failure, caller cancellation) so concurrent same-app requests queue instead
        // of racing each other's inserts.
        var gate = AppGates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadDetailsAsync(appId, forceRefresh, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SteamGameDetails?> LoadDetailsAsync(int appId, bool forceRefresh, CancellationToken cancellationToken)
    {
        // Steam is always the price source of truth and is never hidden by an ITAD failure.
        var details = await steamClient.GetAppDetailsAsync(appId, cancellationToken);
        if (details == null)
        {
            return null;
        }

        string? persistedImageUrl = null;
        int? persistedLowestPriceMinor = null;
        DateTime? persistedLowestPriceAt = null;
        IReadOnlyList<SteamGameOffer> persistedOffers = [];
        DateTime? offersRefreshedAt = null;
        var offersStale = false;
        DateTime? ggDealsRefreshedAt = null;
        var ggDealsStale = false;

        await repository.ExecuteInTransactionAsync(async () =>
        {
            var observedAt = details.ObservedAt;
            var refreshDays = Math.Clamp(
                offersSettings?.RefreshAfterDays ?? DefaultRefreshAfterDays,
                MinRefreshAfterDays,
                MaxRefreshAfterDays);
            var refreshWindowStart = DateTime.UtcNow.AddDays(-refreshDays);

            var game = await repository.GetTrack<SteamGame>()
                .Include(g => g.Offers)
                .FirstOrDefaultAsync(g => g.AppId == details.AppId && g.Region == details.Region, cancellationToken);
            // Lowest price is only comparable inside the same currency; a currency switch restarts the local low.
            var currencyChanged = game != null &&
                !string.Equals(game.Currency, details.Currency, StringComparison.OrdinalIgnoreCase);
            var priceChanged = game == null || game.Currency != details.Currency ||
                game.InitialPriceMinor != details.InitialPriceMinor ||
                game.CurrentPriceMinor != details.CurrentPriceMinor ||
                game.DiscountPercent != details.DiscountPercent;

            if (game == null)
            {
                game = new SteamGame { AppId = details.AppId, Region = details.Region };
                await repository.Save(game);
            }

            game.Name = details.Name;
            game.Type = details.Type;
            game.IsFree = details.IsFree;
            game.Currency = details.Currency;
            game.InitialPriceMinor = details.InitialPriceMinor;
            game.CurrentPriceMinor = details.CurrentPriceMinor;
            game.DiscountPercent = details.DiscountPercent;
            game.ObservedAt = observedAt;

            // Detail artwork (header_image) is richer than search tiny_image; prefer it when Steam returns one.
            if (!string.IsNullOrWhiteSpace(details.ImageUrl))
            {
                game.ImageUrl = details.ImageUrl;
            }

            if (details.CurrentPriceMinor.HasValue && !string.IsNullOrWhiteSpace(details.Currency))
            {
                var hasComparableLow = game.LowestPriceMinor.HasValue && !currencyChanged;
                if (!hasComparableLow || details.CurrentPriceMinor.Value < game.LowestPriceMinor!.Value)
                {
                    game.LowestPriceMinor = details.CurrentPriceMinor.Value;
                    game.LowestPriceAt = observedAt;
                }
            }

            if (priceChanged)
            {
                await repository.Save(new SteamPriceObservation
                {
                    SteamGameId = game.SteamGameId,
                    Currency = details.Currency,
                    InitialPriceMinor = details.InitialPriceMinor,
                    CurrentPriceMinor = details.CurrentPriceMinor,
                    DiscountPercent = details.DiscountPercent,
                    ObservedAt = observedAt
                });
            }

            // Staleness is judged on the snapshot already persisted, before any refresh attempt: no
            // stored offers means there is nothing stale to report.
            offersStale = game.Offers.Count > 0 &&
                (game.OffersRefreshedAt is null || game.OffersRefreshedAt.Value < refreshWindowStart);

            // Only the persisted timestamp drives the window. A missing ITAD id or an empty offer set is
            // not an unconditional trigger, otherwise a game ITAD never matches would hit ITAD forever.
            var needsOffers = forceRefresh
                || game.OffersRefreshedAt is null
                || game.OffersRefreshedAt.Value < refreshWindowStart;

            var removedOffers = new List<GameOffer>();

            if (needsOffers)
            {
                var isComparable = string.Equals(details.Type, GameType, StringComparison.OrdinalIgnoreCase) &&
                    !details.IsFree;
                if (!isComparable)
                {
                    // Nothing comparable to ask ITAD for; drop any snapshot left from when it was
                    // comparable and record the decision so it is not retried every request.
                    removedOffers.AddRange(RemoveOffersBySource(game, ItadSource));
                    game.OffersRefreshedAt = observedAt;
                    offersStale = false;
                }
                else
                {
                    try
                    {
                        removedOffers.AddRange(await RefreshOffersAsync(game, observedAt, cancellationToken));
                        game.OffersRefreshedAt = observedAt;
                        offersStale = false;
                    }
                    catch (Exception exception) when (IsDegradableProviderFailure(exception, cancellationToken))
                    {
                        // Provider down, unparseable, or timed out: keep the persisted snapshot and let
                        // offersStale stand. The timestamp is not advanced, so the next request retries.
                        // A caller-request cancellation fails the filter and aborts the transaction.
                    }
                }
            }

            // gg.deals runs on the same refresh window (there is no separate setting) but on its own
            // gate and its own try/catch: both providers share one rate-limit bucket, so a failure or an
            // exhausted budget in one must never cancel the other's refresh.
            ggDealsStale = game.Offers.Any(offer => string.Equals(offer.Source, GgDealsSource, StringComparison.OrdinalIgnoreCase)) &&
                (game.GgDealsRefreshedAt is null || game.GgDealsRefreshedAt.Value < refreshWindowStart);

            var needsGgDeals = forceRefresh
                || game.GgDealsRefreshedAt is null
                || game.GgDealsRefreshedAt.Value < refreshWindowStart;

            if (needsGgDeals)
            {
                try
                {
                    removedOffers.AddRange(await RefreshGgDealsOffersAsync(game, observedAt, cancellationToken));
                    game.GgDealsRefreshedAt = observedAt;
                    ggDealsStale = false;
                }
                catch (Exception exception) when (IsDegradableProviderFailure(exception, cancellationToken))
                {
                    // Provider down, unparseable, timed out, or out of the shared budget: keep the
                    // persisted snapshot and leave GgDealsRefreshedAt untouched so the next request
                    // retries, with GgDealsStale still reporting the last successful refresh.
                }
            }

            await repository.SaveChangesAsync();

            persistedImageUrl = game.ImageUrl;
            persistedLowestPriceMinor = game.LowestPriceMinor;
            persistedLowestPriceAt = game.LowestPriceAt;
            persistedOffers = game.Offers
                .Where(offer => !removedOffers.Contains(offer))
                .OrderBy(offer => offer.ShopName, StringComparer.OrdinalIgnoreCase)
                .Select(ToOfferModel)
                .ToList();
            offersRefreshedAt = game.OffersRefreshedAt;
            ggDealsRefreshedAt = game.GgDealsRefreshedAt;
            return true;
        });

        return details with
        {
            ImageUrl = persistedImageUrl,
            LowestPriceMinor = persistedLowestPriceMinor,
            LowestPriceAt = persistedLowestPriceAt,
            Offers = persistedOffers,
            OffersRefreshedAt = offersRefreshedAt,
            OffersStale = offersStale,
            GgDealsRefreshedAt = ggDealsRefreshedAt,
            GgDealsStale = ggDealsStale
        };
    }

    /// <summary>
    /// True for provider failures that must degrade to the persisted snapshot instead of failing the
    /// request: transport errors, unparseable payloads, and timeouts. A cancellation requested by the
    /// caller is never degraded, so the request aborts and the transaction rolls back.
    /// </summary>
    private static bool IsDegradableProviderFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or JsonException or OperationCanceledException;

    /// <summary>
    /// Reads the persisted snapshot and schedules every offer of one provider for deletion. Returns the
    /// entities removed so the response can exclude them even before the change tracker reports the delete.
    /// </summary>
    private IReadOnlyList<GameOffer> RemoveOffersBySource(SteamGame game, string source)
    {
        var obsolete = game.Offers
            .Where(offer => string.Equals(offer.Source, source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return RemoveTracked(obsolete);
    }

    /// <summary>
    /// Schedules the offers of one provider that the provider itself no longer returned. Every provider
    /// shares <c>game_offers</c>, so the delete is scoped to <paramref name="source"/>: without that
    /// filter, a refresh of one provider would wipe the rows belonging to the other.
    /// </summary>
    private IReadOnlyList<GameOffer> RemoveObsoleteOffers(SteamGame game, string source, IReadOnlySet<string> returnedKeys)
    {
        var obsolete = game.Offers
            .Where(offer =>
                string.Equals(offer.Source, source, StringComparison.OrdinalIgnoreCase) &&
                !returnedKeys.Contains(offer.OfferKey))
            .ToList();
        return RemoveTracked(obsolete);
    }

    private IReadOnlyList<GameOffer> RemoveTracked(IReadOnlyList<GameOffer> obsolete)
    {
        if (obsolete.Count == 0)
        {
            return [];
        }

        var tracked = repository.GetTrack<GameOffer>();
        foreach (var offer in obsolete)
        {
            tracked.Remove(offer);
        }

        return obsolete;
    }

    /// <summary>
    /// Finds the persisted row for (source, offer key) or creates it, so a refresh replaces the snapshot
    /// instead of inserting a duplicate against the unique constraint.
    /// </summary>
    private GameOffer GetOrCreateOffer(SteamGame game, DbSet<GameOffer> tracked, string source, string offerKey)
    {
        var offer = game.Offers.FirstOrDefault(existing =>
            string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.OfferKey, offerKey, StringComparison.OrdinalIgnoreCase));
        if (offer is not null)
        {
            return offer;
        }

        offer = new GameOffer
        {
            SteamGameId = game.SteamGameId,
            Source = source,
            OfferKey = offerKey
        };
        tracked.Add(offer);
        // EF fixup attaches the tracked entity to game.Offers.
        return offer;
    }

    /// <summary>
    /// ITAD lookup + prices for a single game. Every HTTP call happens before any state is mutated,
    /// so a provider failure from here leaves the persisted offers untouched. Returns the offers whose
    /// deletion this call scheduled (a successful no-match is authoritative and clears the snapshot).
    /// </summary>
    private async Task<IReadOnlyList<GameOffer>> RefreshOffersAsync(SteamGame game, DateTime observedAt, CancellationToken cancellationToken)
    {
        var itadId = await itadClient.LookupSteamAppIdAsync(game.AppId, cancellationToken);
        if (itadId is null)
        {
            // Delisted / unknown / not a comparable item: ITAD returned an authoritative no-match, so the
            // previous snapshot is dropped rather than reported as fresh.
            return RemoveOffersBySource(game, ItadSource);
        }

        game.ItadGameId = itadId;

        var prices = await itadClient.GetPricesAsync([itadId], cancellationToken);
        var match = prices.FirstOrDefault(pricesEntry =>
            string.Equals(pricesEntry.ItadId, itadId, StringComparison.OrdinalIgnoreCase));
        var deals = match?.Deals ?? [];
        var historyLowAll = match?.HistoryLowes?.All;

        // Read-only lookup of the persisted daily rate; this path performs no provider fetch.
        var rate = await fxRateService.GetLatestRateAsync(FxBaseCurrency, FxQuoteCurrency, cancellationToken);

        var offers = repository.GetTrack<GameOffer>();
        var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var deal in deals)
        {
            if (!returnedKeys.Add(deal.ShopId))
            {
                continue;
            }

            ApplyDeal(GetOrCreateOffer(game, offers, ItadSource, deal.ShopId), deal, historyLowAll, rate, observedAt);
        }

        // A successful response (even empty) is authoritative: drop ITAD offers no longer returned.
        return RemoveObsoleteOffers(game, ItadSource, returnedKeys);
    }

    /// <summary>
    /// gg.deals lookup for a single game. The provider reports one current price per bucket (retail and
    /// keyshops) instead of a list of shops, so each bucket becomes one persisted row under a fixed
    /// offer key. Returns the offers whose deletion this call scheduled: an authoritative "not tracked by
    /// gg.deals" answer clears the previous snapshot, exactly like an ITAD no-match.
    /// </summary>
    private async Task<IReadOnlyList<GameOffer>> RefreshGgDealsOffersAsync(SteamGame game, DateTime observedAt, CancellationToken cancellationToken)
    {
        var prices = await ggDealsClient.GetPricesAsync([game.AppId], cancellationToken);

        // No entry for this app id means gg.deals does not track the game; a payload without a currency
        // cannot be stored because the column is not nullable. Both are authoritative answers, not
        // failures: clearing the snapshot lets the caller advance GgDealsRefreshedAt. Otherwise a game
        // gg.deals never matches would be asked for on every single request, forever.
        if (!prices.TryGetValue(game.AppId, out var price) || string.IsNullOrWhiteSpace(price.Currency))
        {
            return RemoveOffersBySource(game, GgDealsSource);
        }

        // Read-only lookup of the persisted daily rate; this path performs no provider fetch.
        var rate = await fxRateService.GetLatestRateAsync(FxBaseCurrency, FxQuoteCurrency, cancellationToken);

        var offers = repository.GetTrack<GameOffer>();
        var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (offerKey, keyshop) in new[] { (GgDealsRetailOfferKey, false), (GgDealsKeyshopOfferKey, true) })
        {
            var currentPriceMinor = keyshop ? price.CurrentKeyshopsMinor : price.CurrentRetailMinor;
            if (currentPriceMinor is null)
            {
                // No price in this bucket: no row is created and any stale one is purged below.
                continue;
            }

            returnedKeys.Add(offerKey);
            ApplyGgDealsBucket(GetOrCreateOffer(game, offers, GgDealsSource, offerKey), price, keyshop, rate, observedAt);
        }

        return RemoveObsoleteOffers(game, GgDealsSource, returnedKeys);
    }

    private static void ApplyDeal(
        GameOffer offer,
        ItadDeal deal,
        ItadAmount? historyLowAll,
        FxRate? rate,
        DateTime observedAt)
    {
        offer.ShopId = deal.ShopId;
        offer.ShopName = deal.ShopName;
        offer.Classification = deal.IsOfficial ? OfficialClassification : AuthorizedClassification;
        offer.DiscountPercent = deal.DiscountPercent;
        offer.DealUrl = deal.DealUrl;
        offer.ObservedAt = observedAt;

        // historyLow.all is the provider-neutral lowest price ever seen. ItadAmount carries its own
        // currency; the deal's currency is only a fallback for a provider payload that omits it.
        if (historyLowAll?.AmountMinor is null)
        {
            offer.HistoryLowAllMinor = null;
            offer.HistoryLowCurrency = null;
        }
        else
        {
            offer.HistoryLowAllMinor = historyLowAll.AmountMinor;
            offer.HistoryLowCurrency = string.IsNullOrWhiteSpace(historyLowAll.Currency)
                ? deal.Currency.Trim().ToUpperInvariant()
                : historyLowAll.Currency.Trim().ToUpperInvariant();
        }

        // Snapshot fields are replaced, never merged, so a provider dropping a DRM or platform is
        // reflected instead of leaving a stale name behind.
        offer.DrmNames = [.. deal.DrmNames];
        offer.PlatformNames = [.. deal.PlatformNames];

        ApplyPricing(offer, deal.Currency, deal.RegularPriceMinor, deal.CurrentPriceMinor, rate);
    }

    /// <summary>
    /// Writes one gg.deals bucket (retail or keyshops) onto its row. gg.deals reports a single current
    /// price per bucket and no base price or discount, so <see cref="GameOffer.OriginalRegularPriceMinor"/>
    /// and <see cref="GameOffer.DiscountPercent"/> stay null by design; its historical low maps to the
    /// provider-neutral history columns.
    /// </summary>
    private static void ApplyGgDealsBucket(
        GameOffer offer,
        GgDealsGamePrice price,
        bool keyshop,
        FxRate? rate,
        DateTime observedAt)
    {
        var currency = price.Currency.Trim().ToUpperInvariant();
        var historyLowMinor = keyshop ? price.HistoricalKeyshopsMinor : price.HistoricalRetailMinor;

        offer.ShopId = null;
        offer.ShopName = keyshop ? GgDealsKeyshopShopName : GgDealsRetailShopName;
        // The retail bucket is an aggregate over official and authorized stores and never identifies
        // which one, so it must not claim officiality; "authorized" is the closest truthful label and
        // the UI renders it without a badge.
        offer.Classification = keyshop ? KeyshopClassification : AuthorizedClassification;
        offer.DiscountPercent = null;
        offer.DealUrl = price.Url;
        offer.ObservedAt = observedAt;

        // Snapshot fields are replaced, never merged; the provider reports neither DRM nor platforms.
        offer.DrmNames = [];
        offer.PlatformNames = [];

        if (historyLowMinor is null)
        {
            offer.HistoryLowAllMinor = null;
            offer.HistoryLowCurrency = null;
        }
        else
        {
            offer.HistoryLowAllMinor = historyLowMinor;
            offer.HistoryLowCurrency = currency;
        }

        ApplyPricing(
            offer,
            currency,
            regularPriceMinor: null,
            currentPriceMinor: keyshop ? price.CurrentKeyshopsMinor : price.CurrentRetailMinor,
            rate);
    }

    /// <summary>
    /// Pricing columns shared by every provider: the original currency, the pricing type and the derived
    /// MXN value with its FX metadata. Amounts stay in integer minor units; only the rate is decimal.
    /// </summary>
    private static void ApplyPricing(
        GameOffer offer,
        string currency,
        int? regularPriceMinor,
        int? currentPriceMinor,
        FxRate? rate)
    {
        var normalized = currency.Trim().ToUpperInvariant();

        offer.OriginalCurrency = normalized;
        // Written here rather than by each caller: applying pricing without persisting the original
        // amount is precisely how a provider ends up silently losing its price.
        offer.OriginalRegularPriceMinor = regularPriceMinor;
        offer.OriginalCurrentPriceMinor = currentPriceMinor;
        // Derived values are recomputed on every refresh; a stale conversion is never carried forward.
        offer.MxnRegularPriceMinor = null;
        offer.MxnCurrentPriceMinor = null;
        offer.FxRate = null;
        offer.FxRateDate = null;
        offer.FxSource = null;

        if (string.Equals(normalized, MxnCurrency, StringComparison.Ordinal))
        {
            offer.MxnRegularPriceMinor = regularPriceMinor;
            offer.MxnCurrentPriceMinor = currentPriceMinor;
            offer.PricingType = RegionalPricing;
            return;
        }

        if (string.Equals(normalized, FxBaseCurrency, StringComparison.Ordinal) && rate is not null)
        {
            offer.MxnRegularPriceMinor = ConvertMinor(regularPriceMinor, rate.Rate);
            offer.MxnCurrentPriceMinor = ConvertMinor(currentPriceMinor, rate.Rate);
            offer.FxRate = rate.Rate;
            offer.FxRateDate = rate.RateDate;
            offer.FxSource = rate.Source;
            offer.PricingType = FxEstimatePricing;
            return;
        }

        offer.PricingType = UnconvertedPricing;
    }

    private static int? ConvertMinor(int? minor, decimal rate)
    {
        if (minor is null)
        {
            return null;
        }

        // Result stays decimal until the range check, so an outsized rate/product cannot overflow int.
        var converted = Math.Round(minor.Value * rate, MidpointRounding.AwayFromZero);
        return converted < int.MinValue || converted > int.MaxValue
            ? null
            : (int)converted;
    }

    private static SteamGameOffer ToOfferModel(GameOffer offer) =>
        new(
            offer.Source,
            offer.OfferKey,
            offer.ShopId,
            offer.ShopName,
            offer.Classification,
            offer.OriginalCurrency,
            offer.OriginalRegularPriceMinor,
            offer.OriginalCurrentPriceMinor,
            offer.MxnRegularPriceMinor,
            offer.MxnCurrentPriceMinor,
            offer.FxRate,
            offer.FxRateDate,
            offer.FxSource,
            offer.PricingType,
            offer.DiscountPercent,
            offer.DealUrl,
            offer.ObservedAt,
            offer.DrmNames ?? [],
            offer.PlatformNames ?? [],
            offer.HistoryLowAllMinor,
            offer.HistoryLowCurrency);

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is required.", nameof(query));
        }

        var normalized = query.Trim();
        if (normalized.Length > 100)
        {
            throw new ArgumentException("Search query cannot exceed 100 characters.", nameof(query));
        }

        return normalized;
    }
}
