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
    private const string FxSourceFallback = "Banxico";

    // Honest tier comparison. status/reason are machine codes; the UI renders the Spanish copy.
    private const string ComparableStatus = "ok";
    private const string NoSavingStatus = "no_saving";
    private const string NoTierPriceReason = "no_tier_price";
    private const string AddonReason = "addon";
    private const string IncompleteItemsReason = "items_incomplete";
    private const string UnpricedItemReason = "item_unpriced";
    private const string NotComparableReason = "not_comparable";
    private const string CurrencyMismatchReason = "currency_mismatch";
    private const string StaleSnapshotReason = "stale_snapshot";

    // Items that take part in the tier comparison: only a plain game has a comparable standalone price.
    private const string ComparableItemType = "game";
    private const int DefaultRefreshAfterDays = 7;
    private const int MinRefreshAfterDays = 1;
    private const int MaxRefreshAfterDays = 90;
    private const int MinSuggestionLength = 2;
    private const int MaxSuggestionLength = 100;
    private const int SuggestionLimit = 10;

    // Bundle display bounds, mirroring the frontend contract: the persisted tier list is capped so a
    // provider payload cannot inflate the row or the response. The client parses further than this only
    // to attribute the bundle to the queried game.
    private const int MaxBundleTiers = 20;
    private const int MaxBundleTierGames = 50;

    /// <summary>
    /// Options for the sanitized tier payload stored in <c>external_bundles.tiers_json</c>. Web defaults
    /// give camelCase names, so the persisted JSON uses the same field names as the API response.
    /// </summary>
    private static readonly JsonSerializerOptions BundleTierJsonOptions = new(JsonSerializerDefaults.Web);

    // Steam snapshot cache window: a GET reuses the persisted row while its ObservedAt is younger than
    // this, and only a stale/absent row or a POST /refresh reaches the Steam store.
    private static readonly TimeSpan SteamRefreshAfter = TimeSpan.FromHours(1);

    // ponytail: one gate per app id, kept for the process lifetime and bounded by the distinct app ids
    // requested; serializes the same-app DB+HTTP transaction so concurrent requests cannot race the
    // unique (app, region) insert or the offer keys. No idle eviction needed at this scale.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> AppGates = new();

    /// <summary>
    /// Outcome of an outbound provider phase. <see cref="Failed"/> keeps the persisted snapshot and its
    /// timestamps; only an authoritative answer may purge rows or advance the refresh window.
    /// </summary>
    private enum ProviderRefreshOutcome
    {
        Refreshed,
        NoMatch,
        Failed
    }

    /// <summary>
    /// A finished ITAD phase (lookup + prices + the DB-only FX read), captured while no transaction is
    /// open. <see cref="ApplyItadRefresh"/> is the separate, DB-only step that writes it.
    /// </summary>
    private sealed record ItadRefreshResult(
        ProviderRefreshOutcome Outcome,
        string? ItadGameId,
        IReadOnlyList<ItadDeal> Deals,
        ItadAmount? HistoryLowAll,
        FxRate? Rate)
    {
        public static ItadRefreshResult NoMatch() =>
            new(ProviderRefreshOutcome.NoMatch, null, [], null, null);

        public static ItadRefreshResult Succeeded(string itadGameId, IReadOnlyList<ItadDeal> deals, ItadAmount? historyLowAll, FxRate? rate) =>
            new(ProviderRefreshOutcome.Refreshed, itadGameId, deals, historyLowAll, rate);

        public static ItadRefreshResult Failed(string? itadGameId) =>
            new(ProviderRefreshOutcome.Failed, itadGameId, [], null, null);
    }

    /// <summary>
    /// A finished gg.deals phase, captured while no transaction is open.
    /// <see cref="ApplyGgDealsRefresh"/> is the separate, DB-only step that writes it.
    /// </summary>
    private sealed record GgDealsRefreshResult(ProviderRefreshOutcome Outcome, GgDealsGamePrice? Price, FxRate? Rate)
    {
        public static GgDealsRefreshResult NoMatch() =>
            new(ProviderRefreshOutcome.NoMatch, null, null);

        public static GgDealsRefreshResult Succeeded(GgDealsGamePrice price, FxRate? rate) =>
            new(ProviderRefreshOutcome.Refreshed, price, rate);

        public static GgDealsRefreshResult Failed() =>
            new(ProviderRefreshOutcome.Failed, null, null);
    }

    /// <summary>
    /// A finished ITAD bundle phase, captured while no transaction is open.
    /// <see cref="ApplyBundlesRefreshAsync"/> is the separate, DB-only step that writes it. Bundles carry
    /// their own outcome so a bundle failure never degrades the offers and vice versa.
    /// </summary>
    private sealed record ItadBundlesRefreshResult(ProviderRefreshOutcome Outcome, IReadOnlyList<ItadBundle> Bundles)
    {
        public static ItadBundlesRefreshResult NoMatch() =>
            new(ProviderRefreshOutcome.NoMatch, []);

        public static ItadBundlesRefreshResult Succeeded(IReadOnlyList<ItadBundle> bundles) =>
            new(ProviderRefreshOutcome.Refreshed, bundles);

        public static ItadBundlesRefreshResult Failed() =>
            new(ProviderRefreshOutcome.Failed, []);
    }

    public async Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var results = await steamClient.SearchAsync(normalized, cancellationToken);
        var observedAt = DateTime.UtcNow;
        var mapped = new List<SteamSearchResult>(results.Count);

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
                // A row written by search holds name/type/artwork only: no details, no provider refresh.
                mapped.Add(result);
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

            // Detail snapshot and provider timestamps come from the persisted row, not from the search hit.
            mapped.Add(result with
            {
                HasDetails = HoldsSteamDetails(game),
                RefreshedAt = LatestRefresh(game.OffersRefreshedAt, game.GgDealsRefreshedAt)
            });
        }

        return mapped;
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
        // Materialized before mapping because HasDetails/LatestRefresh are shared with SearchAsync and
        // cannot be translated to SQL; the query is bounded by SuggestionLimit, so the cost is trivial.
        var games = await repository.Get<SteamGame>()
            .Where(g => g.Region == Region && g.Name.ToLower().Contains(needle))
            .OrderBy(g => g.Name)
            .Take(SuggestionLimit)
            .ToListAsync(cancellationToken);

        return games
            .Select(g => new SteamSearchResult(
                g.AppId,
                g.Name,
                g.Type,
                g.ImageUrl,
                HoldsSteamDetails(g),
                LatestRefresh(g.OffersRefreshedAt, g.GgDealsRefreshedAt)))
            .ToList();
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
        // ---- Read phase: persisted snapshot, no transaction and no provider traffic. ----
        // PostgreSQL is the cache: a detail snapshot observed less than SteamRefreshAfter ago answers a
        // GET without calling Steam. ObservedAt is also the "última actualización" date the UI shows.
        var snapshot = await repository.Get<SteamGame>()
            .Include(game => game.Offers)
            .FirstOrDefaultAsync(game => game.AppId == appId && game.Region == Region, cancellationToken);

        var now = DateTime.UtcNow;
        var refreshDays = Math.Clamp(
            offersSettings?.RefreshAfterDays ?? DefaultRefreshAfterDays,
            MinRefreshAfterDays,
            MaxRefreshAfterDays);
        var refreshWindowStart = now.AddDays(-refreshDays);

        var needsSteam = forceRefresh ||
            snapshot is null ||
            !HoldsSteamDetails(snapshot) ||
            snapshot.ObservedAt == default ||
            snapshot.ObservedAt < now - SteamRefreshAfter;

        // ---- Outbound phase: every provider call happens here, with no transaction open. ----
        var details = needsSteam ? await steamClient.GetAppDetailsAsync(appId, cancellationToken) : null;
        if (needsSteam && details is null)
        {
            // Steam is the price source of truth and is never hidden by an ITAD failure.
            return null;
        }

        // A fresh Steam answer stamps its own ObservedAt; a cached snapshot keeps the stored one, because
        // advancing it would keep the cache fresh forever.
        var observedAt = details?.ObservedAt ?? now;

        // Steam's type/free decide whether ITAD has anything comparable. A cached snapshot carries the
        // values of its last Steam fetch, so the decision does not change when Steam is served from DB.
        var isComparable = string.Equals(details?.Type ?? snapshot?.Type, GameType, StringComparison.OrdinalIgnoreCase) &&
            !(details?.IsFree ?? snapshot?.IsFree ?? false);

        // Only the persisted timestamp drives the window. A missing ITAD id or an empty offer set is not
        // an unconditional trigger, otherwise a game ITAD never matches would hit ITAD forever.
        var needsOffers = forceRefresh ||
            snapshot is null ||
            snapshot.OffersRefreshedAt is null ||
            snapshot.OffersRefreshedAt.Value < refreshWindowStart;

        // A non-comparable game is an authoritative ITAD no-match answered without any HTTP request.
        var itadRefresh = needsOffers
            ? (isComparable
                ? await FetchItadRefreshAsync(appId, cancellationToken)
                : ItadRefreshResult.NoMatch())
            : null;

        // gg.deals runs on the same refresh window (there is no separate setting) but on its own result:
        // both providers share one rate-limit bucket, so a failure in one must never cancel the other.
        var needsGgDeals = forceRefresh ||
            snapshot is null ||
            snapshot.GgDealsRefreshedAt is null ||
            snapshot.GgDealsRefreshedAt.Value < refreshWindowStart;

        var ggDealsRefresh = needsGgDeals
            ? await FetchGgDealsRefreshAsync(appId, cancellationToken)
            : null;

        // Bundles ride their own window and timestamp: an ITAD bundle failure must leave offersStale and
        // the offer timestamp untouched, and a failed offer refresh must still let bundles refresh.
        var needsBundles = forceRefresh ||
            snapshot is null ||
            snapshot.BundlesRefreshedAt is null ||
            snapshot.BundlesRefreshedAt.Value < refreshWindowStart;

        ItadBundlesRefreshResult? bundlesRefresh = null;
        if (needsBundles)
        {
            // A fresh lookup (or its authoritative no-match) decides the identity; otherwise the persisted
            // id is reused, so a due bundle refresh never pays for a second lookup.
            string? itadIdForBundles;
            if (itadRefresh is null || itadRefresh.Outcome == ProviderRefreshOutcome.Failed)
            {
                itadIdForBundles = itadRefresh?.ItadGameId ?? snapshot?.ItadGameId;
            }
            else
            {
                itadIdForBundles = itadRefresh.ItadGameId;
            }

            bundlesRefresh = itadIdForBundles is not null
                ? await FetchItadBundlesAsync(itadIdForBundles, cancellationToken)
                : itadRefresh?.Outcome == ProviderRefreshOutcome.Failed
                    ? ItadBundlesRefreshResult.Failed()
                    : ItadBundlesRefreshResult.NoMatch();
        }

        string region;
        SteamGameDetails baseDetails;
        if (details is not null)
        {
            region = details.Region;
            baseDetails = details;
        }
        else
        {
            // Cached path: the snapshot exists, a missing one would have forced the Steam fetch above.
            var cached = snapshot!;
            region = cached.Region;
            baseDetails = ToPersistedDetails(cached);
        }

        // ---- Persistence phase: one short transaction, DB-only. ----
        // The lambda below performs no provider call: every HTTP request already happened above.
        return await repository.ExecuteInTransactionAsync<SteamGameDetails?>(async () =>
        {
            // Reload tracked: writes must never be based on the detached snapshot read before the HTTP.
            var game = await repository.GetTrack<SteamGame>()
                .Include(existing => existing.Offers)
                .Include(existing => existing.BundleLinks)
                    .ThenInclude(link => link.Bundle)
                .FirstOrDefaultAsync(
                    existing => existing.AppId == appId && existing.Region == region,
                    cancellationToken);

            var removedOffers = new List<GameOffer>();

            if (details is not null)
            {
                game = await PersistSteamSnapshotAsync(game, details, observedAt, cancellationToken);
            }
            else if (game is null)
            {
                // The cached row vanished between the snapshot read and this reload: nothing left to serve.
                return null;
            }

            // Staleness is judged on the snapshot already persisted, before any refresh attempt: no
            // stored offers means there is nothing stale to report.
            var offersStale = game.Offers.Count > 0 &&
                (game.OffersRefreshedAt is null || game.OffersRefreshedAt.Value < refreshWindowStart);

            if (itadRefresh is not null)
            {
                removedOffers.AddRange(ApplyItadRefresh(game, itadRefresh, observedAt));
                if (itadRefresh.Outcome != ProviderRefreshOutcome.Failed)
                {
                    // An authoritative answer (deals, no-match, or non-comparable) advances the window;
                    // a degraded one leaves the persisted timestamp so the next request retries.
                    game.OffersRefreshedAt = observedAt;
                    offersStale = false;
                }
            }

            var ggDealsStale = game.Offers.Any(offer => string.Equals(offer.Source, GgDealsSource, StringComparison.OrdinalIgnoreCase)) &&
                (game.GgDealsRefreshedAt is null || game.GgDealsRefreshedAt.Value < refreshWindowStart);

            if (ggDealsRefresh is not null)
            {
                removedOffers.AddRange(ApplyGgDealsRefresh(game, ggDealsRefresh, observedAt));
                if (ggDealsRefresh.Outcome != ProviderRefreshOutcome.Failed)
                {
                    game.GgDealsRefreshedAt = observedAt;
                    ggDealsStale = false;
                }
            }

            var bundlesStale = game.BundleLinks.Any(link =>
                    string.Equals(link.Source, ItadSource, StringComparison.OrdinalIgnoreCase)) &&
                (game.BundlesRefreshedAt is null || game.BundlesRefreshedAt.Value < refreshWindowStart);

            var removedLinks = new List<ExternalBundleGame>();
            if (bundlesRefresh is not null)
            {
                removedLinks.AddRange(await ApplyBundlesRefreshAsync(game, bundlesRefresh, observedAt, cancellationToken));
                if (bundlesRefresh.Outcome != ProviderRefreshOutcome.Failed)
                {
                    // An authoritative answer (bundles, no-match, or no ITAD identity) advances the window;
                    // a degraded one leaves the timestamp so the next request retries.
                    game.BundlesRefreshedAt = observedAt;
                    bundlesStale = false;
                }
            }

            await repository.SaveChangesAsync();

            if (bundlesRefresh is not null && bundlesRefresh.Outcome != ProviderRefreshOutcome.Failed)
            {
                // Orphan bundles only become visible once the link removals above are flushed, hence the
                // second save inside the same transaction. Offers are never touched by this purge.
                await PurgeOrphanItadBundlesAsync(cancellationToken);
            }

            // Read-only lookup of the day rate: needed only when the response is about to carry bundles.
            var bundleFxRate = game.BundleLinks.Count > 0
                ? await fxRateService.GetLatestRateAsync(FxBaseCurrency, FxQuoteCurrency, cancellationToken)
                : null;

             return baseDetails with
             {
                 ImageUrl = game.ImageUrl,
                LowestPriceMinor = game.LowestPriceMinor,
                LowestPriceAt = game.LowestPriceAt,
                Offers = game.Offers
                    .Where(offer => !removedOffers.Contains(offer))
                    .OrderBy(offer => offer.ShopName, StringComparer.OrdinalIgnoreCase)
                    .Select(ToOfferModel)
                    .ToList(),
                OffersRefreshedAt = game.OffersRefreshedAt,
                OffersStale = offersStale,
                GgDealsRefreshedAt = game.GgDealsRefreshedAt,
                GgDealsStale = ggDealsStale,
                 Bundles = game.BundleLinks
                     .Where(link => !removedLinks.Contains(link) &&
                         link.Bundle is not null &&
                         (link.Bundle.ExpiresAt is null || link.Bundle.ExpiresAt.Value > now))
                     .OrderBy(link => link.Bundle!.ExpiresAt ?? DateTime.MaxValue)
                     .ThenBy(link => link.Bundle!.Title, StringComparer.OrdinalIgnoreCase)
                     .Select(link => ToBundleModel(link, bundleFxRate, bundlesStale))
                     .ToList(),
                BundlesRefreshedAt = game.BundlesRefreshedAt,
                BundlesStale = bundlesStale
            };
        });
    }

    /// <summary>
    /// Steam-only load: one store call, one persisted snapshot, nothing else. There is no ITAD, gg.deals,
    /// bundle or governor traffic, no refresh window is consulted and no provider timestamp or
    /// <c>itad_game_id</c> is written, so this never marks a game as priced. It exists to seed a
    /// <c>steam_games</c> row (name + price) for a game that has never been opened; real provider pricing
    /// still goes through <see cref="GetByAppIdAsync"/>.
    /// </summary>
    public async Task<SteamGameDetails?> GetAppDetailsOnlyAsync(int appId, CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            throw new ArgumentException("AppID must be greater than zero.", nameof(appId));
        }

        var details = await steamClient.GetAppDetailsAsync(appId, cancellationToken);
        if (details is null || !HoldsSteamDetails(details))
        {
            // A payload with no price, currency or free flag is not a detail snapshot: persisting it would
            // create a row that HoldsSteamDetails rejects forever.
            return null;
        }

        // Same per-app gate as the full load: concurrent callers for one app must not race the
        // (app_id, region) unique index. Acquired after the HTTP call, so the network wait is not held.
        var gate = AppGates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Tracked reload under the gate, so the insert/update is based on the current row.
            var game = await repository.GetTrack<SteamGame>()
                .FirstOrDefaultAsync(
                    existing => existing.AppId == appId && existing.Region == details.Region,
                    cancellationToken);

            return await repository.ExecuteInTransactionAsync<SteamGameDetails?>(async () =>
            {
                await PersistSteamSnapshotAsync(game, details, details.ObservedAt, cancellationToken);
                await repository.SaveChangesAsync();
                return details;
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// DB-only: writes one Steam detail snapshot onto the tracked row (creating it when absent) and appends
    /// the price observation when the price changed. No provider call happens here, and nothing but the
    /// Steam scalar snapshot and the local lowest price is written.
    /// </summary>
    private async Task<SteamGame> PersistSteamSnapshotAsync(
        SteamGame? game,
        SteamGameDetails details,
        DateTime observedAt,
        CancellationToken ct)
    {
        // Judged before a brand-new row exists, so an unseen game never has a comparable low.
        // Lowest price is only comparable inside the same currency; a switch restarts the local low.
        var currencyChanged = game is not null &&
            !string.Equals(game.Currency, details.Currency, StringComparison.OrdinalIgnoreCase);
        var priceChanged = game is null || game.Currency != details.Currency ||
            game.InitialPriceMinor != details.InitialPriceMinor ||
            game.CurrentPriceMinor != details.CurrentPriceMinor ||
            game.DiscountPercent != details.DiscountPercent;

        if (game is null)
        {
            game = new SteamGame
            {
                AppId = details.AppId,
                Region = details.Region,
                Name = details.Name
            };
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

        return game;
    }

    /// <summary>
    /// True for provider failures that must degrade to the persisted snapshot instead of failing the
    /// request: transport errors, unparseable payloads, and timeouts. A cancellation requested by the
    /// caller is never degraded, so the request aborts instead of returning a partial result.
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
    /// Outbound ITAD phase: lookup + prices + the DB-only FX read. No transaction is open and nothing is
    /// written here, so provider latency never extends a transaction. Applying the result is
    /// <see cref="ApplyItadRefresh"/>, a separate DB-only step.
    /// </summary>
    private async Task<ItadRefreshResult> FetchItadRefreshAsync(int appId, CancellationToken cancellationToken)
    {
        string? itadId = null;
        try
        {
            itadId = await itadClient.LookupSteamAppIdAsync(appId, cancellationToken);
            if (itadId is null)
            {
                // Delisted / unknown / not a comparable item: ITAD returned an authoritative no-match.
                return ItadRefreshResult.NoMatch();
            }

            var prices = await itadClient.GetPricesAsync([itadId], cancellationToken);
            var match = prices.FirstOrDefault(pricesEntry =>
                string.Equals(pricesEntry.ItadId, itadId, StringComparison.OrdinalIgnoreCase));
            var deals = match?.Deals ?? [];
            var historyLowAll = match?.HistoryLowes?.All;

            // Read-only lookup of the persisted daily rate; this path performs no provider fetch.
            var rate = await fxRateService.GetLatestRateAsync(FxBaseCurrency, FxQuoteCurrency, cancellationToken);

            return ItadRefreshResult.Succeeded(itadId, deals, historyLowAll, rate);
        }
        catch (Exception exception) when (IsDegradableProviderFailure(exception, cancellationToken))
        {
            // Provider down, unparseable, or timed out: keep the persisted snapshot and let the caller
            // leave offersStale standing. The lookup may have succeeded before the price call failed, so
            // its id is still carried for persistence. A caller-request cancellation fails the filter.
            return ItadRefreshResult.Failed(itadId);
        }
    }

    /// <summary>
    /// Outbound bundle phase. No transaction is open and nothing is written here; applying the result is
    /// <see cref="ApplyBundlesRefreshAsync"/>. The overview response is a flat <c>bundles[]</c> list, so a
    /// bundle is attributed to this game only when one of its tier items is the queried id; an empty list
    /// is the provider's authoritative "this game is in no active bundle".
    /// </summary>
    private async Task<ItadBundlesRefreshResult> FetchItadBundlesAsync(string itadId, CancellationToken cancellationToken)
    {
        try
        {
            var bundles = await itadClient.GetBundlesAsync([itadId], cancellationToken);
            var contained = bundles
                .Where(bundle => bundle.AttributionComplete && bundle.MatchedItadIds.Contains(itadId, StringComparer.OrdinalIgnoreCase))
                .ToList();

            return ItadBundlesRefreshResult.Succeeded(contained);
        }
        catch (Exception exception) when (IsDegradableProviderFailure(exception, cancellationToken))
        {
            // Provider down, unparseable, timed out, or out of the shared budget: keep the persisted bundle
            // snapshot and leave BundlesRefreshedAt untouched so the next request retries.
            return ItadBundlesRefreshResult.Failed();
        }
    }

    /// <summary>
    /// Attributes using the full parser membership result, never the capped render tiers. An incomplete
    /// scan is deliberately not attributable: caller preserves its previous snapshot.
    /// </summary>
    /// <summary>
    /// DB-only phase: writes an outbound ITAD result onto the tracked entity. No provider call happens
    /// here. Returns the offers whose deletion this call scheduled (an authoritative no-match clears the
    /// ITAD snapshot); a degraded result schedules nothing.
    /// </summary>
    private IReadOnlyList<GameOffer> ApplyItadRefresh(SteamGame game, ItadRefreshResult refresh, DateTime observedAt)
    {
        // The lookup id is worth keeping even when the price call that followed it failed.
        if (refresh.ItadGameId is not null)
        {
            game.ItadGameId = refresh.ItadGameId;
        }

        if (refresh.Outcome == ProviderRefreshOutcome.Failed)
        {
            return [];
        }

        if (refresh.Outcome == ProviderRefreshOutcome.NoMatch)
        {
            // The previous snapshot is dropped rather than reported as fresh.
            return RemoveOffersBySource(game, ItadSource);
        }

        var offers = repository.GetTrack<GameOffer>();
        var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var deal in refresh.Deals)
        {
            if (!returnedKeys.Add(deal.ShopId))
            {
                continue;
            }

            ApplyDeal(GetOrCreateOffer(game, offers, ItadSource, deal.ShopId), deal, refresh.HistoryLowAll, refresh.Rate, observedAt);
        }

        // A successful response (even empty) is authoritative: drop ITAD offers no longer returned.
        return RemoveObsoleteOffers(game, ItadSource, returnedKeys);
    }

    /// <summary>
    /// DB-only phase: writes an outbound ITAD bundle result onto the tracked entity. No provider call
    /// happens here. The canonical bundle is keyed by (source, bundle_key) and shared between games,
    /// while the relation is per game. Returns the relations whose deletion this call scheduled, so the
    /// response can exclude them even before the change tracker reports the delete.
    /// </summary>
    private async Task<IReadOnlyList<ExternalBundleGame>> ApplyBundlesRefreshAsync(
        SteamGame game,
        ItadBundlesRefreshResult refresh,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        var links = game.BundleLinks
            .Where(link => string.Equals(link.Source, ItadSource, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (refresh.Outcome == ProviderRefreshOutcome.NoMatch)
        {
            // No ITAD identity or no active bundles: the previous snapshot is dropped rather than kept as
            // if it were fresh. Only this game's ITAD relations are touched.
            RemoveBundleLinks(links);
            return links;
        }

        var linkByKey = new Dictionary<string, ExternalBundleGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (link.Bundle is { } related)
            {
                linkByKey.TryAdd(related.BundleKey, link);
            }
        }

        var canonical = new Dictionary<string, ExternalBundle>(StringComparer.OrdinalIgnoreCase);
        var returnedKeys = refresh.Bundles
            .Select(bundle => bundle.BundleKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (returnedKeys.Count > 0)
        {
            // Bundles already persisted for this source, whether or not they are related to this game.
            var persisted = await repository.GetTrack<ExternalBundle>()
                .Where(bundle => bundle.Source == ItadSource && returnedKeys.Contains(bundle.BundleKey))
                .ToListAsync(cancellationToken);
            foreach (var bundle in persisted)
            {
                canonical[bundle.BundleKey] = bundle;
            }
        }

        var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in refresh.Bundles)
        {
            if (!returned.Add(item.BundleKey))
            {
                continue;
            }

            if (!canonical.TryGetValue(item.BundleKey, out var bundle))
            {
                // Atomic claim closes the cross-game race in multi-instance deployments. ON CONFLICT
                // does not swallow a loser: reload below obtains the canonical tracked row.
                await repository.ExecuteSqlRawAsync(
                    "INSERT INTO external_bundles (source, bundle_key, title, observed_at) VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (source, bundle_key) DO NOTHING",
                    ItadSource, item.BundleKey, item.Title, observedAt);
                bundle = await repository.GetTrack<ExternalBundle>()
                    .SingleAsync(existing => existing.Source == ItadSource && existing.BundleKey == item.BundleKey, cancellationToken);
                canonical[item.BundleKey] = bundle;
            }

            ApplyBundle(bundle, item, observedAt);

            if (linkByKey.TryGetValue(item.BundleKey, out var link))
            {
                link.ObservedAt = observedAt;
                continue;
            }

            // Atomic upsert closes the cross-instance race on the relation unique key. Never call Add:
            // the row may have been inserted by another instance between our snapshot read and now.
            await repository.ExecuteSqlRawAsync(
                "INSERT INTO external_bundle_games (external_bundle_id, steam_game_id, source, observed_at) VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (external_bundle_id, steam_game_id) DO UPDATE SET source = EXCLUDED.source, observed_at = EXCLUDED.observed_at",
                bundle.ExternalBundleId, game.SteamGameId, ItadSource, observedAt);

            // Reload the canonical row into this DbContext. Querying the tracked set after the atomic
            // upsert avoids duplicate tracking and EF fixup attaches Bundle and the link to game.BundleLinks.
            var insertedLink = await repository.GetTrack<ExternalBundleGame>()
                .Include(existing => existing.Bundle)
                .SingleAsync(existing =>
                    existing.ExternalBundleId == bundle.ExternalBundleId &&
                    existing.SteamGameId == game.SteamGameId,
                    cancellationToken);
            linkByKey[item.BundleKey] = insertedLink;
            if (!game.BundleLinks.Contains(insertedLink))
            {
                game.BundleLinks.Add(insertedLink);
            }
        }

        // A successful response is authoritative: drop this game's ITAD relations no longer returned.
        var obsolete = links
            .Where(link => link.Bundle is null || !returned.Contains(link.Bundle.BundleKey))
            .ToList();
        RemoveBundleLinks(obsolete);
        return obsolete;
    }

    private static void ApplyBundle(ExternalBundle bundle, ItadBundle source, DateTime observedAt)
    {
        // Snapshot fields are replaced, never merged, so a provider change is reflected instead of leaving
        // a stale title or link behind. Both URLs are stored exactly as the provider sent them and the tier
        // list is sanitized into its display shape; no raw payload is kept.
        bundle.Title = source.Title;
        bundle.ShopId = source.ShopId;
        bundle.ShopName = source.ShopName;
        bundle.PageUrl = source.PageUrl;
        bundle.DealUrl = source.Url;
        bundle.Details = source.Details;
        bundle.PublishedAt = source.PublishedAt;
        bundle.ExpiresAt = source.ExpiresAt;
        bundle.ObservedAt = observedAt;
        bundle.TiersJson = SerializeTiers(MapTiers(source.Tiers));
    }

    /// <summary>
    /// Maps provider tiers to the sanitized persisted shape: item ids are dropped and both lists are capped
    /// to the display contract. The item's current ITAD price is kept because it is what allows the honest
    /// comparison at read time, but no saving is stored as truth.
    /// </summary>
    private static IReadOnlyList<SteamGameBundleTier> MapTiers(IReadOnlyList<ItadBundleTier> tiers)
    {
        var mapped = new List<SteamGameBundleTier>(Math.Min(tiers.Count, MaxBundleTiers));
        foreach (var tier in tiers.Take(MaxBundleTiers))
        {
            var games = new List<SteamGameBundleTierGame>(Math.Min(tier.Games.Count, MaxBundleTierGames));
            foreach (var game in tier.Games.Take(MaxBundleTierGames))
            {
                games.Add(new SteamGameBundleTierGame(
                    game.Title,
                    game.Type,
                    game.CurrentPriceMinor,
                    game.PriceCurrency));
            }

            mapped.Add(new SteamGameBundleTier(
                tier.PriceMinor,
                tier.Currency,
                tier.Addon,
                // The persistence cap is a truncation too: a tier summed over fewer items than its own
                // list is a partial total, so it must be persisted as incomplete.
                tier.ItemsComplete && tier.Games.Count <= MaxBundleTierGames,
                games));
        }

        return mapped;
    }

    private static string? SerializeTiers(IReadOnlyList<SteamGameBundleTier> tiers) =>
        tiers.Count == 0 ? null : JsonSerializer.Serialize(tiers, BundleTierJsonOptions);

    /// <summary>
    /// Reads the persisted tier payload. It was written by <see cref="SerializeTiers"/>, so anything that
    /// does not parse (hand-edited row, foreign writer) degrades to "no tiers" instead of failing a read.
    /// </summary>
    private static IReadOnlyList<SteamGameBundleTier> DeserializeTiers(string? tiersJson)
    {
        if (string.IsNullOrWhiteSpace(tiersJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SteamGameBundleTier>>(tiersJson, BundleTierJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void RemoveBundleLinks(IReadOnlyList<ExternalBundleGame> links)
    {
        if (links.Count == 0)
        {
            return;
        }

        var tracked = repository.GetTrack<ExternalBundleGame>();
        foreach (var link in links)
        {
            tracked.Remove(link);
        }
    }

    /// <summary>
    /// Deletes ITAD bundles left with no relation after the refresh. Scoped to <c>source = itad</c> and to
    /// bundles with no links at all: offers, other games' bundles and other sources are never touched.
    /// </summary>
    private async Task PurgeOrphanItadBundlesAsync(CancellationToken cancellationToken)
    {
        var orphans = await repository.GetTrack<ExternalBundle>()
            .Where(bundle => bundle.Source == ItadSource && !bundle.Links.Any())
            .ToListAsync(cancellationToken);
        if (orphans.Count == 0)
        {
            return;
        }

        var tracked = repository.GetTrack<ExternalBundle>();
        foreach (var orphan in orphans)
        {
            tracked.Remove(orphan);
        }

        await repository.SaveChangesAsync();
    }

    /// <summary>
    /// Outbound gg.deals phase. No transaction is open and nothing is written here; applying the result
    /// is <see cref="ApplyGgDealsRefresh"/>, a separate DB-only step.
    /// </summary>
    private async Task<GgDealsRefreshResult> FetchGgDealsRefreshAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var prices = await ggDealsClient.GetPricesAsync([appId], cancellationToken);

            // No entry for this app id means gg.deals does not track the game; a payload without a
            // currency cannot be stored because the column is not nullable. Both are authoritative
            // answers, not failures. Otherwise a game gg.deals never matches would be asked for on every
            // single request, forever.
            if (!prices.TryGetValue(appId, out var price) || string.IsNullOrWhiteSpace(price.Currency))
            {
                return GgDealsRefreshResult.NoMatch();
            }

            // Read-only lookup of the persisted daily rate; this path performs no provider fetch.
            var rate = await fxRateService.GetLatestRateAsync(FxBaseCurrency, FxQuoteCurrency, cancellationToken);

            return GgDealsRefreshResult.Succeeded(price, rate);
        }
        catch (Exception exception) when (IsDegradableProviderFailure(exception, cancellationToken))
        {
            // Provider down, unparseable, timed out, or out of the shared budget: keep the persisted
            // snapshot and leave GgDealsRefreshedAt untouched so the next request retries.
            return GgDealsRefreshResult.Failed();
        }
    }

    /// <summary>
    /// DB-only phase: writes an outbound gg.deals result onto the tracked entity. The provider reports
    /// one current price per bucket (retail and keyshops) instead of a list of shops, so each bucket
    /// becomes one persisted row under a fixed offer key. Returns the offers whose deletion this call
    /// scheduled: an authoritative "not tracked by gg.deals" answer clears the previous snapshot.
    /// </summary>
    private IReadOnlyList<GameOffer> ApplyGgDealsRefresh(SteamGame game, GgDealsRefreshResult refresh, DateTime observedAt)
    {
        if (refresh.Outcome == ProviderRefreshOutcome.Failed)
        {
            return [];
        }

        if (refresh.Outcome == ProviderRefreshOutcome.NoMatch)
        {
            return RemoveOffersBySource(game, GgDealsSource);
        }

        var price = refresh.Price!;
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
            ApplyGgDealsBucket(GetOrCreateOffer(game, offers, GgDealsSource, offerKey), price, keyshop, refresh.Rate, observedAt);
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

    private static SteamGameBundle ToBundleModel(ExternalBundleGame link, FxRate? fxRate, bool bundlesStale)
    {
        var bundle = link.Bundle!;
        return new(
            link.Source,
            bundle.BundleKey,
            bundle.Title,
            bundle.ShopId,
            bundle.ShopName,
            bundle.PageUrl,
            bundle.DealUrl,
            bundle.Details,
            bundle.PublishedAt,
            bundle.ExpiresAt,
            bundle.ObservedAt,
            DeserializeTiers(bundle.TiersJson)
                .Select(tier => DeriveTierComparison(tier, fxRate, bundlesStale))
                .ToList());
    }

    /// <summary>
    /// Honest comparison of one tier against the current ITAD prices of its own items. Comparability rules:
    /// every eligible item has a current ITAD price and all of them (tier included) share one currency, so
    /// the total and the savings are never a mixed-currency fabrication. The result is derived here, from
    /// persisted data, and never stored: it is not truth, it is a reading.
    /// </summary>
    private static SteamGameBundleTier DeriveTierComparison(SteamGameBundleTier tier, FxRate? fxRate, bool bundlesStale)
    {
        // A stale snapshot cannot publish a savings figure at all, whatever the tier's own data says: the
        // numbers underneath may no longer be in force. The bundle is still shown.
        if (bundlesStale)
        {
            return TierWithoutSavings(tier, StaleSnapshotReason);
        }

        if (!tier.ItemsComplete)
        {
            return TierWithoutSavings(tier, IncompleteItemsReason);
        }

        if (tier.Addon)
        {
            return TierWithoutSavings(tier, AddonReason);
        }

        if (tier.PriceMinor is null || tier.Currency is null)
        {
            return TierWithoutSavings(tier, NoTierPriceReason);
        }

        // Only comparable items take part: a bundle can legitimately contain DLC or packages, they are
        // displayed anyway but they are not games with a comparable standalone price.
        var eligible = tier.Games
            .Where(game => string.Equals(game.Type, ComparableItemType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (eligible.Count == 0)
        {
            return TierWithoutSavings(tier, NotComparableReason);
        }

        if (eligible.Any(game => game.PriceMinor is null || game.PriceCurrency is null))
        {
            return TierWithoutSavings(tier, UnpricedItemReason);
        }

        var currencies = eligible
            .Select(game => game.PriceCurrency!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (currencies.Count != 1 || !string.Equals(currencies[0], tier.Currency, StringComparison.Ordinal))
        {
            return TierWithoutSavings(tier, CurrencyMismatchReason);
        }

        var individualTotalMinor = eligible.Sum(game => game.PriceMinor!.Value);
        var savingsMinor = individualTotalMinor - tier.PriceMinor.Value;
        var savingsPercent = individualTotalMinor > 0
            ? (int?)Math.Round(savingsMinor * 100m / individualTotalMinor, MidpointRounding.AwayFromZero)
            : null;

        var comparable = tier with
        {
            Status = ComparableStatus,
            Reason = null,
            IndividualTotalMinor = individualTotalMinor,
            SavingsMinor = savingsMinor,
            SavingsPercent = savingsPercent
        };

        // FX estimate is optional and exactly one: a USD tier may be converted with the day's rate. Any
        // other currency is shown unconverted and labelled, never implied.
        if (string.Equals(tier.Currency, FxBaseCurrency, StringComparison.Ordinal) && fxRate is not null)
        {
            return comparable with
            {
                FxRate = fxRate.Rate,
                FxRateDate = fxRate.RateDate,
                FxSource = fxRate.Source,
                PricingType = FxEstimatePricing,
                MxnIndividualTotalMinor = ConvertMinor(individualTotalMinor, fxRate.Rate),
                MxnSavingsMinor = ConvertMinor(savingsMinor, fxRate.Rate)
            };
        }

        return comparable;
    }

    /// <summary>
    /// A tier without a complete, comparable comparison is shown with its published price and the short
    /// reason, and carries no savings figure at all.
    /// </summary>
    private static SteamGameBundleTier TierWithoutSavings(SteamGameBundleTier tier, string reason) =>
        tier with { Status = NoSavingStatus, Reason = reason };

    /// <summary>
    /// True when the persisted row actually holds a Steam detail snapshot. <see cref="SearchAsync"/> also
    /// writes steam_games rows (name/type/artwork only), so age alone is not enough: serving one of those
    /// from the cache would return a detail page without a price. Such a row always re-fetches Steam.
    /// </summary>
    private static bool HoldsSteamDetails(SteamGame game) =>
        game.IsFree ||
        game.Currency is not null ||
        game.CurrentPriceMinor is not null ||
        game.InitialPriceMinor is not null ||
        game.OffersRefreshedAt is not null;

    /// <summary>
    /// The <see cref="HoldsSteamDetails(SteamGame)"/> criteria applied to a fresh Steam payload (the row
    /// carries no provider timestamp yet): a snapshot with no price, currency or free flag is not a detail
    /// snapshot and is not worth persisting.
    /// </summary>
    private static bool HoldsSteamDetails(SteamGameDetails details) =>
        details.IsFree ||
        details.Currency is not null ||
        details.CurrentPriceMinor is not null ||
        details.InitialPriceMinor is not null;

    /// <summary>
    /// Newest provider refresh timestamp, used by the search/suggestion results. Null only when neither
    /// ITAD nor gg.deals has ever answered for the row.
    /// </summary>
    private static DateTime? LatestRefresh(DateTime? offersRefreshedAt, DateTime? ggDealsRefreshedAt)
    {
        if (offersRefreshedAt is null)
        {
            return ggDealsRefreshedAt;
        }

        return ggDealsRefreshedAt is null || offersRefreshedAt > ggDealsRefreshedAt
            ? offersRefreshedAt
            : ggDealsRefreshedAt;
    }

    /// <summary>
    /// Maps the cached row to the response base for a GET served from PostgreSQL. Offer prices, image,
    /// lowest price and provider timestamps are supplied by the caller's <c>with</c> expression from the
    /// tracked entity; only the Steam scalar snapshot is taken from here.
    /// </summary>
    private static SteamGameDetails ToPersistedDetails(SteamGame game) =>
        new(
            game.AppId,
            game.Name,
            game.Type,
            game.IsFree,
            game.Currency,
            game.InitialPriceMinor,
            game.CurrentPriceMinor,
            game.DiscountPercent,
            game.Region,
            game.ObservedAt);

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
