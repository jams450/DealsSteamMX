using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Services.Notifications;

/// <summary>Read-only source of the live candidate for an outbox row, used by stale revalidation.</summary>
public interface IDealAlertCandidateSource
{
    Task<DealAlertCandidate?> FindCurrentAsync(DealAlertEvent alert, CancellationToken cancellationToken);
}

/// <summary>Read-only source of the fresh snapshot set the producer evaluates.</summary>
public interface IDealAlertCandidateReader : IDealAlertCandidateSource
{
    /// <summary>
    /// Every fresh persisted candidate, without an interest filter. Kept for parity with the shared catalog
    /// work; the notifier never calls it in production.
    /// </summary>
    Task<IReadOnlyList<DealAlertCandidate>> LoadFreshAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        CancellationToken cancellationToken);

    /// <summary>
    /// The fresh candidates of one operator's interests, filtered in SQL by <c>game_id</c>/<c>steam_game_id</c>
    /// before any offer payload is materialized (spec S6). A recipient with no interests reads nothing.
    /// </summary>
    Task<IReadOnlyList<DealAlertCandidate>> LoadFreshForRecipientAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        DealAlertRecipient recipient,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only view over persisted snapshots: <c>game_offers</c> (ITAD, gg.deals, Epic, Microsoft) plus the Steam
/// direct price stored on <c>steam_games</c>. It never calls a provider, never writes a price and never touches
/// bundles: it is the "fresh persisted comparable GAME price snapshot" the spec asks the notifier to consume.
///
/// Every candidate-producing query is gated on <see cref="DealAlertPolicy.Region"/> in SQL — the provider rows by
/// <c>game_offers.region</c>, the Steam direct rows by <c>steam_games.region</c> — so the notifier reads the same
/// MX evidence the discover catalog shows and materializes nothing from another region. That prefilter is an
/// optimization and a defence in depth, never the rule: <see cref="DealAlertPolicy.IsComparable"/> re-applies the
/// region gate to every mapped candidate, and each mapping carries the row's OWN region rather than a constant,
/// so a foreign row can neither be loaded nor masquerade as MX if it ever were.
///
/// Steam direct rows are included because S2 lets them qualify on price or score. They are the one source with
/// no provider history low: <see cref="DealAlertPolicy.HasVerifiedHistoryLow"/> refuses <c>source='steam'</c>
/// outright, and the mapping below passes no history low at all, so the local minimum our own observations
/// created can never be claimed as a record.
/// </summary>
public sealed class DealAlertCandidateReader(IRepository repository) : IDealAlertCandidateReader
{
    private const string MxnCurrency = "MXN";
    private const string SteamSource = "steam";
    private const string SteamShopName = "Steam";
    private const string SteamClassification = "official";
    private const string SteamPricingType = "regional";

    /// <summary>All fresh offers, without an interest filter. The producer never calls this in production.</summary>
    public async Task<IReadOnlyList<DealAlertCandidate>> LoadFreshAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        CancellationToken cancellationToken)
    {
        var threshold = nowUtc - maxSnapshotAge;

        var offers = await repository.Get<GameOffer>()
            .Where(offer => offer.Region == DealAlertPolicy.Region)
            .Where(offer => offer.ObservedAt >= threshold)
            .ToListAsync(cancellationToken);

        return await BuildAsync(offers, [], cancellationToken);
    }

    /// <summary>
    /// Fresh candidates restricted to the operator's interest set. The filter runs in SQL, so a person with
    /// three wanted games loads three games' offers instead of the whole catalog (spec S6).
    /// </summary>
    public async Task<IReadOnlyList<DealAlertCandidate>> LoadFreshForRecipientAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        DealAlertRecipient recipient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        if (recipient.GameIds.Count == 0 && recipient.SteamGameIds.Count == 0)
        {
            return [];
        }

        var threshold = nowUtc - maxSnapshotAge;
        var gameIds = recipient.GameIds.ToList();
        var steamGameIds = recipient.SteamGameIds.ToList();

        var offers = await repository.Get<GameOffer>()
            .Where(offer => offer.Region == DealAlertPolicy.Region)
            .Where(offer => offer.ObservedAt >= threshold)
            .Where(offer =>
                (offer.GameId != null && gameIds.Contains(offer.GameId.Value)) ||
                (offer.SteamGameId != null && steamGameIds.Contains(offer.SteamGameId.Value)))
            .ToListAsync(cancellationToken);

        // Steam direct price: only a native MX MXN amount is a comparable MXN price; a row of another region or
        // another currency would be a conversion this reader must not invent.
        var directSteamGameIds = await repository.Get<SteamGame>()
            .Where(game => game.Region == DealAlertPolicy.Region)
            .Where(game => game.ObservedAt >= threshold)
            .Where(game => game.CurrentPriceMinor != null && game.CurrentPriceMinor > 0 && game.Currency == MxnCurrency)
            .Where(game => (game.GameId != null && gameIds.Contains(game.GameId.Value)) ||
                steamGameIds.Contains(game.SteamGameId))
            .Select(game => game.SteamGameId)
            .ToListAsync(cancellationToken);

        return await BuildAsync(offers, directSteamGameIds, cancellationToken);
    }

    /// <summary>
    /// Re-reads the snapshot behind an outbox row. Returns null when the row no longer exists or no longer
    /// belongs to the same canonical game; the dispatcher then retires it as stale.
    /// </summary>
    public async Task<DealAlertCandidate?> FindCurrentAsync(DealAlertEvent alert, CancellationToken cancellationToken)
    {
        if (string.Equals(alert.Source, SteamSource, StringComparison.Ordinal))
        {
            return await FindCurrentSteamAsync(alert, cancellationToken);
        }

        var offers = await repository.Get<GameOffer>()
            .Where(offer => offer.Region == DealAlertPolicy.Region)
            .Where(offer => offer.Source == alert.Source && offer.OfferKey == alert.OfferKey)
            .Where(offer => alert.SteamGameId == null || offer.SteamGameId == alert.SteamGameId)
            .Where(offer => alert.GameId == null || offer.GameId == alert.GameId)
            .OrderByDescending(offer => offer.ObservedAt)
            .ToListAsync(cancellationToken);

        var offer = offers.FirstOrDefault();
        if (offer is null)
        {
            return null;
        }

        SteamGame? steam = null;
        if (offer.SteamGameId is { } steamGameId)
        {
            steam = await repository.Get<SteamGame>()
                .FirstOrDefaultAsync(game => game.SteamGameId == steamGameId, cancellationToken);
        }

        Game? game = null;
        if (offer.GameId is { } gameId)
        {
            game = await repository.Get<Game>()
                .FirstOrDefaultAsync(candidate => candidate.GameId == gameId, cancellationToken);
        }

        int? low = null;
        if (offer.SteamGameId is { } historyGameId)
        {
            var lows = await LoadMxnHistoryLowsAsync([historyGameId], cancellationToken);
            lows.TryGetValue(historyGameId, out var value);
            low = value;
        }

        return Map(offer, steam, game, low);
    }

    /// <summary>
    /// Steam direct revalidation. The offer key is the appid, the exact Steam snapshot is the identity, and a
    /// canonical game that moved (or a row that is no longer MXN) is retired instead of sent.
    /// </summary>
    private async Task<DealAlertCandidate?> FindCurrentSteamAsync(
        DealAlertEvent alert,
        CancellationToken cancellationToken)
    {
        var appId = ParseAppId(alert.OfferKey);
        if (appId <= 0)
        {
            return null;
        }

        var query = repository.Get<SteamGame>().Where(game => game.Region == DealAlertPolicy.Region);
        query = alert.SteamGameId is { } steamGameId
            ? query.Where(game => game.SteamGameId == steamGameId)
            : query.Where(game => game.AppId == appId);

        var steam = await query.OrderBy(game => game.SteamGameId).FirstOrDefaultAsync(cancellationToken);
        if (steam is null || steam.AppId != appId)
        {
            return null;
        }

        // GameId exact: the alert was enqueued for one canonical game, and the same deal under another
        // identity is a different dedup key, not a row to send.
        if (alert.GameId is { } gameId && steam.GameId != gameId)
        {
            return null;
        }

        if (!string.Equals(steam.Currency, MxnCurrency, StringComparison.Ordinal) ||
            steam.CurrentPriceMinor is not > 0)
        {
            return null;
        }

        var lows = await LoadMxnHistoryLowsAsync([steam.SteamGameId], cancellationToken);
        lows.TryGetValue(steam.SteamGameId, out var low);
        return MapSteam(steam, low);
    }

    /// <summary>
    /// Shared projection. <paramref name="offers"/> is the persisted <c>game_offers</c> set;
    /// <paramref name="directSteamGameIds"/> are the already-filtered fresh MXN Steam direct rows.
    /// </summary>
    private async Task<IReadOnlyList<DealAlertCandidate>> BuildAsync(
        IReadOnlyList<GameOffer> offers,
        IReadOnlyList<int> directSteamGameIds,
        CancellationToken cancellationToken)
    {
        if (offers.Count == 0 && directSteamGameIds.Count == 0)
        {
            return [];
        }

        var steamGameIds = offers
            .Where(offer => offer.SteamGameId is not null)
            .Select(offer => offer.SteamGameId!.Value)
            .Concat(directSteamGameIds)
            .Distinct()
            .ToList();
        var gameIds = offers
            .Where(offer => offer.SteamGameId is null && offer.GameId is not null)
            .Select(offer => offer.GameId!.Value)
            .Distinct()
            .ToList();

        var steamGames = steamGameIds.Count == 0
            ? new Dictionary<int, SteamGame>()
            : await repository.Get<SteamGame>()
                .Where(game => steamGameIds.Contains(game.SteamGameId))
                .ToDictionaryAsync(game => game.SteamGameId, cancellationToken);

        // A Steam snapshot can anchor the canonical game even when no offer row carries the game id.
        gameIds.AddRange(steamGames.Values
            .Where(game => game.GameId is not null)
            .Select(game => game.GameId!.Value));
        gameIds = gameIds.Distinct().ToList();
        var games = gameIds.Count == 0
            ? new Dictionary<long, Game>()
            : await repository.Get<Game>()
                .Where(game => gameIds.Contains(game.GameId))
                .ToDictionaryAsync(game => game.GameId, cancellationToken);

        var historyLows = await LoadMxnHistoryLowsAsync(steamGameIds, cancellationToken);

        var candidates = new List<DealAlertCandidate>(offers.Count + directSteamGameIds.Count);
        foreach (var offer in offers)
        {
            steamGames.TryGetValue(offer.SteamGameId ?? -1, out var steam);
            games.TryGetValue(offer.GameId ?? -1, out var game);
            var low = offer.SteamGameId is { } steamGameId && historyLows.TryGetValue(steamGameId, out var value)
                ? value
                : null;
            candidates.Add(Map(offer, steam, game, low));
        }

        foreach (var steamGameId in directSteamGameIds)
        {
            if (!steamGames.TryGetValue(steamGameId, out var steam))
            {
                continue;
            }

            historyLows.TryGetValue(steamGameId, out var low);
            candidates.Add(MapSteam(steam, low));
        }

        return candidates;
    }

    private async Task<Dictionary<int, int?>> LoadMxnHistoryLowsAsync(
        IReadOnlyCollection<int> steamGameIds,
        CancellationToken cancellationToken)
    {
        if (steamGameIds.Count == 0)
        {
            return [];
        }

        var rows = await repository.Get<GameOffer>()
            .Where(offer => offer.SteamGameId != null && steamGameIds.Contains(offer.SteamGameId.Value))
            .GroupBy(offer => offer.SteamGameId)
            .Select(group => new
            {
                SteamGameId = group.Key!.Value,
                Low = group.Min(offer =>
                    offer.HistoryLowCurrency == MxnCurrency ? offer.HistoryLowAllMinor : null),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.SteamGameId, row => row.Low);
    }

    private static DealAlertCandidate Map(GameOffer offer, SteamGame? steam, Game? game, int? mxnHistoryLow) =>
        new(
            offer.GameId,
            offer.SteamGameId,
            !string.IsNullOrWhiteSpace(steam?.Name) ? steam!.Name : game?.Title ?? offer.ShopName,
            offer.Source,
            offer.OfferKey,
            offer.ShopName,
            offer.Classification,
            offer.PricingType,
            offer.Region,
            offer.OriginalCurrency,
            offer.OriginalCurrentPriceMinor,
            offer.MxnCurrentPriceMinor ?? 0,
            offer.HistoryLowAllMinor,
            offer.HistoryLowCurrency,
            offer.ObservedAt,
            steam?.InitialPriceMinor,
            steam?.Currency,
            mxnHistoryLow,
            mxnHistoryLow is null ? null : MxnCurrency,
            offer.DealUrl);

    /// <summary>
    /// Steam direct snapshot. <c>HistoryLowAllMinor</c> is null on purpose: our own observations created
    /// <c>steam_games.lowest_price_minor</c>, so passing it would let the price rule masquerade as a record. The
    /// score still gets the provider-reported MXN low (<paramref name="mxnHistoryLow"/>) and the Steam list price
    /// as its discount base.
    ///
    /// The region is the snapshot's OWN (<c>steam.Region</c>), never a hardcoded <c>mx</c>: the policy has to be
    /// able to see that a foreign row is foreign and refuse it, instead of reading every row as a Mexican price.
    /// </summary>
    private static DealAlertCandidate MapSteam(SteamGame steam, int? mxnHistoryLow) =>
        new(
            steam.GameId,
            steam.SteamGameId,
            steam.Name,
            SteamSource,
            steam.AppId.ToString(CultureInfo.InvariantCulture),
            SteamShopName,
            SteamClassification,
            SteamPricingType,
            steam.Region,
            steam.Currency ?? MxnCurrency,
            steam.CurrentPriceMinor,
            steam.CurrentPriceMinor ?? 0,
            null,
            null,
            steam.ObservedAt,
            steam.InitialPriceMinor,
            steam.Currency,
            mxnHistoryLow,
            mxnHistoryLow is null ? null : MxnCurrency,
            SteamStoreUrl(steam.AppId));

    private static string SteamStoreUrl(int appId) =>
        string.Create(CultureInfo.InvariantCulture, $"https://store.steampowered.com/app/{appId}/");

    private static int ParseAppId(string? storeGameId) =>
        int.TryParse(storeGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : 0;
}
