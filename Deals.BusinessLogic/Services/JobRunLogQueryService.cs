using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

public sealed record JobRunQuerySummary(int Total, int Running, int Ok, int Failed, DateTime? LastStartedAt);
public sealed record JobRunQueryItem(long JobRunId, string Job, string Trigger, string Status, DateTime StartedAt, DateTime? FinishedAt, long? DurationMilliseconds, JsonElement? Details);
public sealed record JobRunQueryPage(JobRunQuerySummary Summary, IReadOnlyList<JobRunQueryItem> Items, int Page, int PageSize, int TotalPages);

public sealed class JobRunQueryService(IRepository repository)
{
    public async Task<JobRunQueryPage> QueryAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 10_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = repository.Get<JobRun>();
        var total = await query.CountAsync(cancellationToken);
        var running = await query.CountAsync(x => x.Status == JobRunStatuses.Running, cancellationToken);
        var ok = await query.CountAsync(x => x.Status == JobRunStatuses.Ok, cancellationToken);
        var failed = await query.CountAsync(x => x.Status == JobRunStatuses.Failed, cancellationToken);
        var last = await query.OrderByDescending(x => x.StartedAt).Select(x => (DateTime?)x.StartedAt).FirstOrDefaultAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.StartedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = rows.Select(ToItem).ToList();
        return new(new(total, running, ok, failed, last), items, page, pageSize, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
    }

    private static JobRunQueryItem ToItem(JobRun run)
    {
        JsonElement? details = null;
        if (!string.IsNullOrWhiteSpace(run.Details))
        {
            try { details = JsonSerializer.Deserialize<JsonElement>(run.Details); } catch (JsonException) { }
        }
        var duration = run.FinishedAt.HasValue ? (long?)(run.FinishedAt.Value - run.StartedAt).TotalMilliseconds : null;
        return new(run.JobRunId, run.Job, run.Trigger, run.Status, run.StartedAt, run.FinishedAt, duration, details);
    }
}
