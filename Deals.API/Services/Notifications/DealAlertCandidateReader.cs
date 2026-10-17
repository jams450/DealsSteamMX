using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Notifications;
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
    Task<IReadOnlyList<DealAlertCandidate>> LoadFreshAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only view over persisted <c>game_offers</c> snapshots. It never calls a provider, never writes a
/// price and never touches bundles: it is the "fresh persisted comparable GAME price snapshot" the spec
/// asks the notifier to consume.
///
/// The Steam direct snapshot is deliberately absent as a candidate: it is the discount base (and the
/// referee the UI already shows), and its local minimum is self-observed, so it is not a deal to push.
/// </summary>
public sealed class DealAlertCandidateReader(IRepository repository) : IDealAlertCandidateReader
{
    private const string MxnCurrency = "MXN";

    /// <summary>All fresh offers, optionally capped. Used by the producer; the policy still re-checks freshness.</summary>
    public async Task<IReadOnlyList<DealAlertCandidate>> LoadFreshAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        CancellationToken cancellationToken)
    {
        var threshold = nowUtc - maxSnapshotAge;

        var offers = await repository.Get<GameOffer>()
            .Where(offer => offer.ObservedAt >= threshold)
            .ToListAsync(cancellationToken);

        if (offers.Count == 0)
        {
            return [];
        }

        var steamGameIds = offers
            .Where(offer => offer.SteamGameId is not null)
            .Select(offer => offer.SteamGameId!.Value)
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
        var games = gameIds.Count == 0
            ? new Dictionary<long, Game>()
            : await repository.Get<Game>()
                .Where(game => gameIds.Contains(game.GameId))
                .ToDictionaryAsync(game => game.GameId, cancellationToken);
        var historyLows = await LoadMxnHistoryLowsAsync(steamGameIds, cancellationToken);

        var candidates = new List<DealAlertCandidate>(offers.Count);
        foreach (var offer in offers)
        {
            steamGames.TryGetValue(offer.SteamGameId ?? -1, out var steam);
            games.TryGetValue(offer.GameId ?? -1, out var game);
            var low = offer.SteamGameId is { } steamGameId && historyLows.TryGetValue(steamGameId, out var value)
                ? value
                : null;
            candidates.Add(Map(offer, steam, game, low));
        }

        return candidates;
    }

    /// <summary>
    /// Re-reads the offer behind an outbox row. Returns null when the provider no longer has that offer.
    /// </summary>
    public async Task<DealAlertCandidate?> FindCurrentAsync(DealAlertEvent alert, CancellationToken cancellationToken)
    {
        var offers = await repository.Get<GameOffer>()
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
}
