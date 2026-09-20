using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Stores;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>One library row this pass can price, already reduced to what the store call needs.</summary>
internal sealed record LibraryStorePriceCandidate(long GameId, string Title, string PackageFamilyName);

/// <summary>
/// Writes the store offer of the library games Steam cannot price, today the Xbox ones.
///
/// <para>
/// A Steam row is not required: the offer is anchored on <c>game_id</c> with a null <c>steam_game_id</c>,
/// which is what the nullable column exists for. Freshness is read from the offer's own <c>observed_at</c>
/// instead of a per-steam-game timestamp column, because the game being priced may have no Steam row to
/// hang such a column on, and the unit of staleness is (game, source) anyway.
/// </para>
/// </summary>
public sealed class LibraryStorePriceService(
    IRepository repository,
    MicrosoftStoreClient microsoftStoreClient,
    ILogger<LibraryStorePriceService> logger) : ILibraryStorePriceService
{
    /// <summary>Same region the rest of the code reads and writes.</summary>
    private const string Region = "mx";

    /// <summary>The store sells one base offer per game, so the key is fixed and a changed id updates the row.</summary>
    private const string OfferKey = "store";

    private const string OfficialClassification = "official";

    private const string RegionalPricing = "regional";

    private const string UnconvertedPricing = "unconverted";

    private const string MxnCurrency = "MXN";

    /// <summary>How long a written offer is trusted before the pass asks the store again.</summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    public async Task<LibraryStorePriceSyncResult> SyncStorePricesAsync(
        int userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > LibraryStorePriceLimits.Max)
        {
            throw new ArgumentException(
                $"El límite debe estar entre 1 y {LibraryStorePriceLimits.Max} juegos",
                nameof(limit));
        }

        var loaded = await LoadCandidatesAsync(userId, cancellationToken);
        var pending = await FilterStaleAsync(loaded.Candidates, cancellationToken);
        var batch = pending.Take(limit).ToList();

        // When the canonical game also has a Steam snapshot, the offer carries both anchors: the Steam one
        // keeps it inside the aggregates the library and the wishlist already build (they group by the Steam
        // snapshot), and the canonical one is what makes it possible at all when there is none. One query for
        // the whole batch, not one per game.
        var steamGameIdByGameId = await ResolveSteamGameIdsAsync(batch, cancellationToken);

        var tracked = repository.GetTrack<GameOffer>();
        // Offers the batch already has, stale ones included: they are updated in place, not inserted again
        // (the canonical key would raise 23505). One query for the whole batch.
        var existingByGameId = await LoadExistingAsync(tracked, batch, cancellationToken);
        var updated = 0;
        var failed = 0;
        var rejected = 0;

        foreach (var candidate in batch)
        {
            StoreOffer? offer;
            try
            {
                offer = await microsoftStoreClient.FindOfferAsync(
                    candidate.Title,
                    candidate.PackageFamilyName,
                    cancellationToken);
            }
            catch (Exception exception) when (IsDegradableProviderFailure(exception, cancellationToken))
            {
                logger.LogWarning(
                    exception,
                    "[library.storeprice] Microsoft Store lookup failed for game {GameId} ({StoreId}).",
                    candidate.GameId,
                    candidate.PackageFamilyName);
                failed++;
                continue;
            }

            if (offer is null)
            {
                // The product exists but has no purchasable price in this market, or the id is not sold here.
                failed++;
                continue;
            }

            // The StoreId this call resolved is the canonical id. Claiming it before writing the price means a
            // library row pointing at another game's product cannot price the wrong canonical game.
            var claimed = await GameIdentityClaimer.TryClaimAsync(
                repository,
                candidate.GameId,
                GameExternalIdNamespaces.Xbox,
                offer.ExternalId,
                cancellationToken);

            if (!claimed)
            {
                logger.LogWarning(
                    "[library.storeprice] Store id {StoreId} already belongs to another game; skipped {GameId}.",
                    offer.ExternalId,
                    candidate.GameId);
                rejected++;
                continue;
            }

            if (!existingByGameId.TryGetValue(candidate.GameId, out var row))
            {
                row = new GameOffer
                {
                    GameId = candidate.GameId,
                    // No Steam row is required to hold a store price: this is the whole point of the phase.
                    SteamGameId = steamGameIdByGameId.TryGetValue(candidate.GameId, out var steamGameId)
                        ? steamGameId
                        : null,
                    Region = Region,
                    Source = MicrosoftStoreClient.StoreSource,
                    OfferKey = OfferKey
                };
                tracked.Add(row);
                existingByGameId[candidate.GameId] = row;
            }

            ApplyRegionalOffer(row, offer, DateTime.UtcNow);
            updated++;
        }

        // One save for the whole pass: a failure halfway still lands every price fetched so far.
        if (updated > 0)
        {
            await repository.SaveChangesAsync();
        }

        return new LibraryStorePriceSyncResult(
            pending.Count,
            loaded.Unsupported,
            updated,
            failed,
            rejected,
            pending.Count - batch.Count);
    }

    /// <summary>Candidates of one pass plus how many library rows had to be dropped for lack of a usable id.</summary>
    private sealed record LoadedCandidates(List<LibraryStorePriceCandidate> Candidates, int Unsupported);

    /// <summary>
    /// Library rows this pass could handle, with the unusable ones already dropped. A row without a usable
    /// PackageFamilyName is not a failure: the store id it carries is not the kind of id the catalog accepts.
    /// Two rows may point at the same canonical game, and one price is enough.
    /// </summary>
    private async Task<LoadedCandidates> LoadCandidatesAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var raw = await repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId && row.Store == StoreKeys.Xbox && row.GameId != null)
            .Select(row => new { GameId = row.GameId!.Value, row.StoreGameId, row.Title })
            .ToListAsync(cancellationToken);

        var seen = new HashSet<long>();
        var unsupported = 0;
        var candidates = new List<LibraryStorePriceCandidate>(raw.Count);

        foreach (var row in raw)
        {
            if (MicrosoftStoreClient.NormalizePackageFamilyName(row.StoreGameId) is not { } packageFamilyName)
            {
                unsupported++;
                continue;
            }

            if (!seen.Add(row.GameId))
            {
                continue;
            }

            candidates.Add(new LibraryStorePriceCandidate(row.GameId, row.Title, packageFamilyName));
        }

        return new LoadedCandidates(candidates, unsupported);
    }

    /// <summary>
    /// The given rows that have no store offer inside the refresh window. Reading the offer instead of a gate
    /// column keeps this pass independent of <c>steam_games</c>, which these games do not have.
    /// </summary>
    private async Task<List<LibraryStorePriceCandidate>> FilterStaleAsync(
        List<LibraryStorePriceCandidate> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var gameIds = rows.Select(row => row.GameId).ToList();
        var freshSince = DateTime.UtcNow - RefreshAfter;

        var fresh = await repository.Get<GameOffer>()
            .Where(offer => offer.GameId != null &&
                gameIds.Contains(offer.GameId.Value) &&
                offer.Source == MicrosoftStoreClient.StoreSource &&
                offer.Region == Region &&
                offer.ObservedAt >= freshSince)
            .Select(offer => offer.GameId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var freshIds = fresh.ToHashSet();
        return rows.Where(row => !freshIds.Contains(row.GameId)).ToList();
    }

    /// <summary>
    /// The store offer the batch already holds, indexed by canonical game. A row that exists but is stale is
    /// part of this map on purpose: it is the row to rewrite.
    /// </summary>
    private async Task<Dictionary<long, GameOffer>> LoadExistingAsync(
        DbSet<GameOffer> tracked,
        List<LibraryStorePriceCandidate> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return [];
        }

        var gameIds = batch.Select(candidate => candidate.GameId).ToList();
        var existing = await tracked
            .Where(offer => offer.GameId != null &&
                gameIds.Contains(offer.GameId.Value) &&
                offer.Source == MicrosoftStoreClient.StoreSource &&
                offer.Region == Region &&
                offer.OfferKey == OfferKey)
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, GameOffer>(existing.Count);
        foreach (var offer in existing)
        {
            map.TryAdd(offer.GameId!.Value, offer);
        }

        return map;
    }

    /// <summary>
    /// Steam snapshot of each batched canonical game, when there is one. Absent from the map means the game
    /// has no Steam row, which is the case the nullable <c>steam_game_id</c> exists for.
    /// </summary>
    private async Task<Dictionary<long, int>> ResolveSteamGameIdsAsync(
        List<LibraryStorePriceCandidate> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return [];
        }

        var gameIds = batch.Select(candidate => candidate.GameId).ToList();
        var snapshots = await repository.Get<SteamGame>()
            .Where(snapshot => snapshot.GameId != null &&
                gameIds.Contains(snapshot.GameId.Value) &&
                snapshot.Region == Region)
            .Select(snapshot => new { GameId = snapshot.GameId!.Value, snapshot.SteamGameId })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, int>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            map.TryAdd(snapshot.GameId, snapshot.SteamGameId);
        }

        return map;
    }

    /// <summary>
    /// Writes one direct store offer. Every field is replaced, never merged, so a store dropping its price
    /// cannot leave a stale amount behind.
    ///
    /// <para>
    /// Deliberately a copy of <c>SteamGameService.ApplyStoreOffer</c> and not a shared helper: that one is
    /// private to a verified path, and what the two actually share is a handful of assignments around a
    /// static method that takes an FX rate this pass never has. Extract it when a third store-direct writer
    /// appears; until then this is the cheaper of the two honest options.
    /// </para>
    /// </summary>
    private static void ApplyRegionalOffer(GameOffer row, StoreOffer offer, DateTime observedAt)
    {
        row.ShopId = offer.ShopId;
        row.ShopName = offer.ShopName;
        row.Classification = OfficialClassification;
        row.DiscountPercent = offer.DiscountPercent;
        row.DealUrl = offer.DealUrl;
        row.ObservedAt = observedAt;

        // A direct store answer carries no provider-level historical low and no DRM/platform list, so the
        // previous values are cleared rather than left behind as if this call had reported them.
        row.HistoryLowAllMinor = null;
        row.HistoryLowCurrency = null;
        row.DrmNames = [];
        row.PlatformNames = [];

        var currency = offer.Currency.Trim().ToUpperInvariant();
        row.OriginalCurrency = currency;
        row.OriginalRegularPriceMinor = offer.RegularPriceMinor;
        row.OriginalCurrentPriceMinor = offer.CurrentPriceMinor;

        // No conversion happens on this path: the catalog was asked for a market and answers in its own
        // currency, so any previous FX-derived amount is dropped rather than carried forward.
        row.FxRate = null;
        row.FxRateDate = null;
        row.FxSource = null;

        if (!string.Equals(currency, MxnCurrency, StringComparison.Ordinal))
        {
            // A market answering in another currency has no MXN amount to compare, and inventing one with an
            // FX rate is exactly what the Epic phase removed. The row stays unconverted and unranked.
            row.MxnRegularPriceMinor = null;
            row.MxnCurrentPriceMinor = null;
            row.PricingType = UnconvertedPricing;
            return;
        }

        row.MxnRegularPriceMinor = offer.RegularPriceMinor;
        row.MxnCurrentPriceMinor = offer.CurrentPriceMinor;
        row.PricingType = RegionalPricing;
    }

    private static bool IsDegradableProviderFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or JsonException or OperationCanceledException;
}
