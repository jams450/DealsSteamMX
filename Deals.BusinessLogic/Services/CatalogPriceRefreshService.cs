using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Steam;

using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// One bounded, manual pass over the games on one user's wishlist that the monitor cannot call recent (spec
/// S5/S6, user correction «Sí, panel y job solo de mi wishlist»).
///
/// Candidate selection is recomputed from persisted rows on every run — there is no queue and no pending flag
/// to go stale. The set is the monitor's own <c>stale</c>+<c>missing</c> buckets — where `stale` means the
/// CHEAPEST usable comparable price is old, not the newest observation of any shop — <em>restricted to the
/// requester's wishlist</em>, ordered missing first and then oldest chosen price first, filtered by the stricter
/// per-provider gate and cut to <see cref="CatalogPriceRefreshLimits.BatchSize"/> in SQL.
///
/// The refresh itself is the existing per-game pipeline with <see cref="GameRefreshMode.StalePriceBatch"/>:
/// the same per-app gate (concurrent same-app work queues instead of racing), the same provider governors and
/// cooldowns, the same "a failed answer never advances a timestamp" rule, and the provider TTL the requester chose
/// instead of the configured 7-day one. It never forces the interactive Full mode. When due, the Steam phase calls
/// GetAppDetailsAsync and persists the returned store snapshot, including price and metadata; the ITAD phase
/// resolves provider identity when needed. Both run under the batch's independent gates, all derived from the same
/// <c>ageHours</c>: the bucket of the cheapest stored price, the per-phase due gates, the selection, the
/// before/after measurement and the remaining count. 72 is only the caller's default — no per-game code re-reads a
/// fixed 72 h window, and the monitor's own window is not consulted.
///
/// The wishlist is the only scope: the library (<c>owned</c>), the favourites and other accounts are not read,
/// and the requester's id is a required argument, so an anonymous run is impossible by construction rather
/// than by convention.
///
/// Steam-less games are reported as <see cref="CatalogPriceRefreshOutcome.Unsupported"/> and are never
/// selected: this pass has no catalog-wide Xbox identity path, and inventing a Steam id or a price for them
/// would be worse than saying so. They also stay counted in <see cref="CatalogPriceRefreshOutcome.Remaining"/>,
/// because they are, in fact, still pending.
/// </summary>
public sealed class CatalogPriceRefreshService(
    IRepository repository,
    ISteamGameService steamGameService,
    TimeProvider clock,
    ILogger<CatalogPriceRefreshService> logger,
    ICatalogWishlistScopeResolver wishlistScope) : ICatalogPriceRefreshService
{
    public async Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
        int requestedByUserId,
        CancellationToken cancellationToken = default,
        int ageHours = CatalogPriceRefreshLimits.DefaultAgeHours)
    {
        // The window is validated before any read: a run that would reprice a window nobody asked for must not spend
        // a query, let alone a provider call, and it must not record an answer for it either.
        CatalogPriceRefreshLimits.ValidateAgeHours(ageHours, nameof(ageHours));

        // Fail closed before any read: a pass whose owner is unknown would be the old catalogue-wide sweep.
        var scope = await wishlistScope.LoadAsync(requestedByUserId, cancellationToken);
        if (scope.IsEmpty)
        {
            // Nothing to price, and the entries the scope could not attribute still travel: an unreadable
            // wishlist is not an empty one.
            return new CatalogPriceRefreshOutcome(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                UnresolvedWishlistEntries: scope.UnresolvedEntries,
                AgeHours: ageHours);
        }

        var asOfUtc = clock.GetUtcNow().UtcDateTime;
        var offers = repository.Get<GameOffer>();
        var steamGames = repository.Get<SteamGame>();
        var prices = CatalogFreshnessQueries.PriceUniverse(offers, steamGames, asOfUtc);
        var pending = CatalogFreshnessQueries.Pending(CatalogFreshnessQueries.GameStates(
            CatalogFreshnessQueries.WishlistGames(repository.Get<Game>(), scope.GameIds),
            prices,
            asOfUtc,
            ageHours));

        var pendingCount = await pending.CountAsync(cancellationToken);
        if (pendingCount == 0)
        {
            return new CatalogPriceRefreshOutcome(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                UnresolvedWishlistEntries: scope.UnresolvedEntries,
                AgeHours: ageHours);
        }

        // The pending set split by WHY it is pending (spec S1/S3): a price older than the chosen window, or no price
        // at all. Both are included by default — the user's own choice — and reporting them apart is what keeps the
        // record honest about which half was repriced.
        var pendingByStatus = await CatalogFreshnessQueries.Counts(pending).ToListAsync(cancellationToken);
        var olderPricePending = pendingByStatus
            .Where(row => string.Equals(row.Status, CatalogFreshnessStatus.Stale, StringComparison.Ordinal))
            .Sum(row => row.Total);
        var missingPricePending = pendingByStatus
            .Where(row => string.Equals(row.Status, CatalogFreshnessStatus.Missing, StringComparison.Ordinal))
            .Sum(row => row.Total);

        // The two pending games this pass will not attempt, counted instead of hidden: no Steam identity at
        // all, and "already asked inside the window with nothing comparable back".
        var unsupported = await CatalogFreshnessQueries
            .PendingWithoutSteam(pending, steamGames)
            .CountAsync(cancellationToken);
        var notDue = await CatalogFreshnessQueries
            .PendingNotDue(pending, steamGames, asOfUtc, ageHours)
            .CountAsync(cancellationToken);

        var candidates = await CatalogFreshnessQueries
            .PendingCandidates(pending, steamGames, CatalogPriceRefreshLimits.BatchSize, asOfUtc, ageHours)
            .ToListAsync(cancellationToken);

        var updated = 0;
        var skipped = 0;
        var gateDeclined = 0;
        var noComparablePrice = 0;
        var degraded = 0;
        var errors = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SteamStalePriceRefresh attempt;
            try
            {
                attempt = await steamGameService.RefreshStalePricesAsync(
                    candidate.AppId,
                    cancellationToken,
                    ageHours);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // An exception that escaped the refresh is reported, never hidden: the row keeps its stamps, so
                // the next run retries this game instead of cooling it down.
                errors++;
                logger.LogWarning(
                    exception,
                    "[catalog.prices-refresh] refresh failed for game {GameId} (app {AppId}).",
                    candidate.GameId,
                    candidate.AppId);
                continue;
            }

            // "Did the price really move" is measured on the same winner the monitor buckets by — the cheapest
            // usable comparable MXN price, with its amount and instant — never with a second definition and never
            // with "the newest observation of any shop". A phase that renews a more expensive Steam price leaves
            // the panel's price exactly where it was, so it is a skip and not a price update (user correction S9).
            var after = await ChosenPriceAsync(candidate.GameId, ageHours, cancellationToken);
            var hasUsablePriceAfter = after?.ChosenObservedAt is not null;
            var advanced = CatalogPriceRefreshMeasurement.Advanced(
                candidate.ChosenPriceMinor,
                candidate.ChosenObservedAt,
                after?.ChosenPriceMinor,
                after?.ChosenObservedAt);

            switch (CatalogPriceRefreshClassification.Classify(attempt, hasUsablePriceAfter, advanced))
            {
                case CatalogPriceRefreshVerdict.Updated:
                    updated++;
                    break;
                case CatalogPriceRefreshVerdict.NoComparablePrice:
                    noComparablePrice++;
                    break;
                case CatalogPriceRefreshVerdict.Degraded:
                    degraded++;
                    logger.LogInformation(
                        "[catalog.prices-refresh] game {GameId} degraded: due={Due} advanced={Advanced} failed={Failed}",
                        candidate.GameId,
                        string.Join(',', attempt.DuePhases),
                        string.Join(',', attempt.AdvancedPhases),
                        string.Join(',', attempt.DegradedPhases));
                    break;
                default:
                    // A declined gate asked nothing; a skip answered "nothing new". They are different facts.
                    if (!attempt.Ran)
                    {
                        gateDeclined++;
                    }
                    else
                    {
                        skipped++;
                    }

                    break;
            }
        }

        var remainingAsOfUtc = clock.GetUtcNow().UtcDateTime;
        // The remaining count is measured with the SAME window: a run that moved a 24 h boundary and then counted the
        // rest at 72 h would report a wishlist it did not leave behind.
        var remaining = await CatalogFreshnessQueries
            .Pending(CatalogFreshnessQueries.GameStates(
                CatalogFreshnessQueries.WishlistGames(repository.Get<Game>(), scope.GameIds),
                CatalogFreshnessQueries.PriceUniverse(offers, steamGames, remainingAsOfUtc),
                remainingAsOfUtc,
                ageHours))
            .CountAsync(cancellationToken);

        logger.LogInformation(
            "[catalog.prices-refresh] user={UserId} ageHours={AgeHours} pending={Pending} " +
            "olderPricePending={OlderPricePending} missingPricePending={MissingPricePending} " +
            "selected={Selected} updated={Updated} " +
            "skipped={Skipped} gateDeclined={GateDeclined} noComparable={NoComparable} unsupported={Unsupported} " +
            "notDue={NotDue} degraded={Degraded} errors={Errors} remaining={Remaining} " +
            "unresolvedEntries={UnresolvedEntries}",
            requestedByUserId,
            ageHours,
            pendingCount,
            olderPricePending,
            missingPricePending,
            candidates.Count,
            updated,
            skipped,
            gateDeclined,
            noComparablePrice,
            unsupported,
            notDue,
            degraded,
            errors,
            remaining,
            scope.UnresolvedEntries);

        return new CatalogPriceRefreshOutcome(
            pendingCount,
            candidates.Count,
            updated,
            skipped,
            gateDeclined,
            noComparablePrice,
            unsupported,
            notDue,
            degraded,
            errors,
            remaining,
            scope.UnresolvedEntries,
            ageHours,
            olderPricePending,
            missingPricePending);
    }

    /// <summary>
    /// The chosen price of one game right now — the same universe, comparability rule and winner selection the
    /// monitor buckets with, so "did this pass change the price the panel shows" is measured with the panel's own
    /// definition and not with a second one. Returns <c>null</c> only if the game row itself disappeared.
    /// </summary>
    private async Task<CatalogFreshnessGameState?> ChosenPriceAsync(
        long gameId,
        int ageHours,
        CancellationToken cancellationToken)
    {
        var asOfUtc = clock.GetUtcNow().UtcDateTime;
        var prices = CatalogFreshnessQueries.PriceUniverse(
            repository.Get<GameOffer>(),
            repository.Get<SteamGame>(),
            asOfUtc);

        return await CatalogFreshnessQueries
            .GameStates(repository.Get<Game>().Where(game => game.GameId == gameId), prices, asOfUtc, ageHours)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
