using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Jobs;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Reads the execution history (<c>job_runs</c>) for the admin Jobs page. Read-only and paginated: the
/// table grows with every scheduled cycle, so nothing here loads it whole.
///
/// Two contracts matter beyond the obvious projection:
/// <list type="bullet">
/// <item><see cref="QueryAsync"/> applies the same exact-match filter to the summary and to the page, so
/// the counters always describe the rows the operator is looking at instead of the whole table.</item>
/// <item>An unreadable <c>details</c> JSON is returned as absent rather than as a 500: a corrupt payload
/// from an older binary must not make the history unreadable.</item>
/// </list>
/// </summary>
public sealed class JobRunLogQueryService(IRepository repository) : IJobRunLogQueryService
{
    private const int MaxFilterLength = 64;

    public async Task<JobRunQueryPage> QueryAsync(JobRunQueryFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var page = Math.Clamp(filter.Page, 1, 10_000);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var job = Normalize(filter.Job, nameof(filter.Job));
        var status = Normalize(filter.Status, nameof(filter.Status));
        var trigger = Normalize(filter.Trigger, nameof(filter.Trigger));

        var query = repository.Get<JobRun>();
        if (job is not null) query = query.Where(run => run.Job == job);
        if (status is not null) query = query.Where(run => run.Status == status);
        if (trigger is not null) query = query.Where(run => run.Trigger == trigger);

        var total = await query.CountAsync(cancellationToken);
        var running = await query.CountAsync(run => run.Status == JobRunStatuses.Running, cancellationToken);
        var ok = await query.CountAsync(run => run.Status == JobRunStatuses.Ok, cancellationToken);
        var failed = await query.CountAsync(run => run.Status == JobRunStatuses.Failed, cancellationToken);
        var last = await query
            .OrderByDescending(run => run.StartedAt)
            .Select(run => (DateTime?)run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.JobRunId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var applied = new JobRunQueryFilter(job, status, trigger, page, pageSize);
        return new JobRunQueryPage(
            new JobRunQuerySummary(total, running, ok, failed, last),
            rows.Select(ToItem).ToList(),
            page,
            pageSize,
            Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
            applied);
    }

    public async Task<JobRunQueryItem?> GetAsync(long jobRunId, CancellationToken cancellationToken)
    {
        var row = await repository.Get<JobRun>()
            .Where(run => run.JobRunId == jobRunId)
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToItem(row);
    }

    public async Task<IReadOnlyList<JobRunLatest>> GetLatestByJobAsync(
        IReadOnlyCollection<string> jobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var results = new List<JobRunLatest>(jobs.Count);
        // One bounded query per job (the catalog has seven). A group-by with "first row of the group" is not
        // translatable in EF Core, and loading the table to pick one row per job in memory is exactly the
        // unbounded read this service exists to avoid.
        foreach (var job in jobs.Distinct(StringComparer.Ordinal))
        {
            var row = await repository.Get<JobRun>()
                .Where(run => run.Job == job)
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.JobRunId)
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                continue;
            }

            results.Add(new JobRunLatest(
                row.Job,
                row.JobRunId,
                row.Trigger,
                row.Status,
                row.StartedAt,
                row.FinishedAt,
                Duration(row),
                ParseDetails(row.Details)));
        }

        return results;
    }

    public Task<bool> HasRunningAsync(string job, TimeSpan abandonedAfter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(job);

        var cutoff = DateTime.UtcNow - (abandonedAfter < TimeSpan.Zero ? TimeSpan.Zero : abandonedAfter);
        return repository.Get<JobRun>()
            .AnyAsync(
                run => run.Job == job && run.Status == JobRunStatuses.Running && run.StartedAt >= cutoff,
                cancellationToken);
    }

    private static JobRunQueryItem ToItem(JobRun run) =>
        new(
            run.JobRunId,
            run.Job,
            run.Trigger,
            run.Status,
            run.StartedAt,
            run.FinishedAt,
            Duration(run),
            ParseDetails(run.Details));

    private static long? Duration(JobRun run) => Duration(run.StartedAt, run.FinishedAt);

    private static long? Duration(DateTime startedAt, DateTime? finishedAt) =>
        finishedAt.HasValue ? (long?)(finishedAt.Value - startedAt).TotalMilliseconds : null;

    private static JsonElement? ParseDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(details);
        }
        catch (JsonException)
        {
            // Older or corrupt payload: the row still exists, its detail simply is not readable JSON.
            return null;
        }
    }

    private static string? Normalize(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxFilterLength)
        {
            throw new ArgumentException($"El filtro no puede superar {MaxFilterLength} caracteres.", parameterName);
        }

        return trimmed;
    }
}
