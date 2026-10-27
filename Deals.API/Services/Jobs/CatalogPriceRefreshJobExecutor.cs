using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Diagnostics;

namespace Deals.API.Services.Jobs;

/// <summary>
/// <c>catalog.prices-refresh</c>: the manual-only bounded pass over the pending prices of the requesting user's
/// wishlist (spec S5/S6, user correction «Sí, panel y job solo de mi wishlist»).
///
/// It has no schedule and no hosted service: the only way it runs is somebody queuing it from the admin Jobs
/// page, which is the whole point — re-pricing is an explicit operational decision, not background traffic.
/// The batch is the constant <see cref="CatalogPriceRefreshLimits.BatchSize"/>, paced by the existing provider
/// governors (one admission per HTTP attempt). The dispatcher takes this job's own advisory lock, preventing
/// concurrent runs of this same job across instances; different jobs have separate locks.
///
/// The window is the run's own <c>ageHours</c> option (spec S2/S3) — 1..720, default 72 — and it is the single number
/// the pass uses for the bucket, the gates, the selection, the measurement and the remaining count. The record
/// repeats it, together with the split between games whose price is merely old and games with no price at all, so a
/// history entry can never be read as if it had used the monitor's fixed 72 h.
///
/// Ownership: the person who queued the run is captured from the access token at submission and travels in the
/// queue item, so this executor receives a user id and never reads the HTTP context. Called without one — the
/// plain <see cref="IJobExecutor"/> entry point — it refuses and does not touch the pass at all: there is no
/// catalog-wide batch and no fallback account behind this job.
///
/// The wishlist may also hold entries no exact identity can attribute to a game. They are never priced and get no
/// synthetic game row; the run reports them as <c>unresolvedWishlistEntries</c> and does not describe a zero
/// selection as "nothing pending" while they exist.
///
/// Outcome policy: a run with any degraded phase or escaping exception is never <c>ok</c> — the refresh
/// pipeline absorbs provider faults, so counting them is the only way they become visible. A run whose whole
/// pending set is unpricedable (no Steam identity anywhere) is also failed, with that exact reason: the
/// "unsupported only" verdict is only true when the unsupported count *is* the pending count, and a mixed set
/// gets its own honest reason instead of borrowing that one.
/// </summary>
public sealed class CatalogPriceRefreshJobExecutor(
    ICatalogPriceRefreshService refreshService,
    // Optional like the other scoped observers: a hand-built executor (checks, tools) keeps working and simply
    // produces no diagnosis.
    ISyncDiagnosticsCollector? diagnostics = null)
    : IUserScopedJobExecutor
{
    public const string JobKey = JobKeys.CatalogPricesRefresh;

    /// <summary>
    /// This job's own session lock, distinct from the other jobs' keys (wishlist 091, ntfy 092, the bundle
    /// sweeps 093/094/095, fx 096, purge 097). It serialises <em>this</em> job against itself: two queued manual
    /// requests, or a second instance of the host, cannot reprice the same batch concurrently. It is not a
    /// shared lock with the wishlist pass or any other job, and it is not a per-game lock — that is the app gate
    /// inside <c>SteamGameService</c>.
    /// </summary>
    public const long AdvisoryLockKey = 7_831_442_098L;

    public string Job => JobKey;

    /// <summary>
    /// Refused on purpose. A wishlist-scoped pass has no meaning without an owner, and running the previous
    /// catalogue-wide sweep here would be exactly the behaviour this correction removed. The dispatcher routes
    /// this executor through <see cref="ExecuteAsync(JobRunOptions, int, CancellationToken)"/>.
    /// </summary>
    public Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(new JobExecutionResult(false, new { reason = "requester_required" }));

    public async Task<JobExecutionResult> ExecuteAsync(
        JobRunOptions options,
        int requestedByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (requestedByUserId <= 0)
        {
            // Belt and braces: the dispatcher already refuses, and the pass would refuse too. A refused run claims
            // nothing, so it cannot produce a report either.
            return new JobExecutionResult(false, new { reason = "requester_required" });
        }

        // The run owns the diagnosis of this scope (spec S2/S3): claimed before the pass, and only here, so an
        // interactive request — which shares the service but never the claim — records nothing.
        diagnostics?.Activate();

        // The chosen window travels as an argument, not as configuration: this job never reads a TTL, so it cannot
        // inherit the 7-day provider window and it is not pinned to a fixed 72 h either.
        var ageHours = options.AgeHours;
        var outcome = await refreshService.RefreshPendingAsync(requestedByUserId, cancellationToken, ageHours);

        // The reasons are part of the record: "0 selected" alone cannot be told apart from a broken run.
        string? reason = null;
        var succeeded = outcome.Errors == 0 && outcome.Degraded == 0;

        if (outcome.Pending == 0)
        {
            // "Nothing pending" is only the whole truth when the wishlist itself is fully readable. Entries that
            // could not be attributed are reported instead of being folded into a clean empty result.
            reason = outcome.UnresolvedWishlistEntries > 0
                ? "nothing_pending_unresolved_entries"
                : "nothing_pending";
        }
        else if (outcome.Selected == 0)
        {
            // Every pending game is unpricedable, or cooled down, or a mix of both. Each verdict is named
            // exactly: "unsupported only" is reserved for the case where unsupported really is the whole set.
            var allUnsupported = outcome.Unsupported == outcome.Pending;
            var allNotDue = outcome.NotDue == outcome.Pending;

            if (allUnsupported)
            {
                // Nothing was attempted because nothing could be: the limitation is reported, not celebrated.
                succeeded = false;
                reason = "pending_unsupported_only";
            }
            else if (allNotDue)
            {
                reason = "no_provider_due";
            }
            else
            {
                reason = "pending_unsupported_and_not_due";
            }
        }

        return new JobExecutionResult(succeeded, new
        {
            reason,
            requestedBy = requestedByUserId,
            // The window this run really used, so the counts below are read against the hours that produced them.
            ageHours = outcome.AgeHours,
            pending = outcome.Pending,
            olderPricePending = outcome.OlderPricePending,
            missingPricePending = outcome.MissingPricePending,
            selected = outcome.Selected,
            updated = outcome.Updated,
            skipped = outcome.Skipped,
            gateDeclined = outcome.GateDeclined,
            noComparablePrice = outcome.NoComparablePrice,
            unsupported = outcome.Unsupported,
            notDue = outcome.NotDue,
            degraded = outcome.Degraded,
            errors = outcome.Errors,
            remaining = outcome.Remaining,
            unresolvedWishlistEntries = outcome.UnresolvedWishlistEntries,
            // Per-game detail of the same pass (spec S2/S3): which provider phase of which game refreshed, had
            // nothing to offer or failed, and why. Null when no phase ran, never an invented empty report.
            diagnostics = diagnostics is { HasEvents: true } collected ? collected.BuildSnapshot() : null,
        });
    }
}
