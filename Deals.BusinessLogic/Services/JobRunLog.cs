using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Execution log of the periodic HostedServices (<c>job_runs</c>). Two jobs share one table and one
/// helper: the gate (<see cref="RanWithinAsync"/>) answers "did this job already run?", and the
/// start/finish pair records each cycle whatever its outcome. No interface: it is a plain DB helper with
/// a single implementation, injected where the jobs already create a scope.
/// </summary>
public sealed class JobRunLog(IRepository repository)
{
    public const string WishlistSync = "wishlist.sync";

    /// <summary>Recovery pass right after the host started.</summary>
    public const string StartupTrigger = "startup";

    /// <summary>Run of the configured daily slot.</summary>
    public const string ScheduledTrigger = "scheduled";

    /// <summary>Admin-triggered run. Excluded from the window: it never performs the expensive price pass.</summary>
    public const string ManualTrigger = "manual";

    /// <summary>
    /// True when <paramref name="job"/> started any run (whatever its status) inside the window. A run left
    /// in <c>running</c> counts too: the process died mid-cycle and replaying it immediately is exactly what
    /// this gate exists to prevent.
    /// </summary>
    public Task<bool> RanWithinAsync(string job, TimeSpan window, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - window;

        return repository.Get<JobRun>().AnyAsync(
            run => run.Job == job && run.Trigger != ManualTrigger && run.StartedAt > cutoff,
            cancellationToken);
    }

    /// <summary>Opens a run in <c>running</c> and returns its id for <see cref="FinishAsync"/>.</summary>
    public async Task<long> StartAsync(string job, string trigger, CancellationToken cancellationToken)
    {
        var run = new JobRun
        {
            Job = job,
            Trigger = trigger,
            Status = JobRunStatuses.Running,
            StartedAt = DateTime.UtcNow
        };

        repository.GetTrack<JobRun>().Add(run);
        await repository.SaveChangesAsync();

        return run.JobRunId;
    }

    /// <summary>
    /// Closes the run. The caller owns the failure: a log write must never fail the cycle, and the gate
    /// still sees the row's <c>started_at</c> even when this call dies. A <paramref name="jobRunId"/> of 0
    /// means the run was never recorded, so this returns without touching the database.
    /// </summary>
    public async Task FinishAsync(long jobRunId, string status, object? details, CancellationToken cancellationToken)
    {
        // Tracked lookup by hand: IRepository.GetByIdAsync only accepts int, and JobRunId is a BIGSERIAL.
        var run = await repository.GetTrack<JobRun>()
            .FirstOrDefaultAsync(candidate => candidate.JobRunId == jobRunId, cancellationToken);
        if (run is null)
        {
            return;
        }

        run.Status = status;
        run.FinishedAt = DateTime.UtcNow;
        run.Details = details is null ? null : JsonSerializer.Serialize(details);

        await repository.SaveChangesAsync();
    }
}
