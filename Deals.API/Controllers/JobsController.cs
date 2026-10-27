using System.Text.Json;
using Deals.API.Models.Jobs;
using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Jobs;
using Deals.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Deals.API.Controllers;

/// <summary>
/// Admin control centre for the registered background jobs: what ran, what each job is, and — the point of
/// spec S2 — "run this one now, with these bounded options, and tell me what happened".
///
/// The whole controller is <c>AdminWithId</c>: it exposes operational health, provider budgets and the one
/// destructive action in the product. Nothing here returns configuration secrets: details are sanitised on
/// the way out (<see cref="JobDetailsSanitizer"/>).
///
/// The requester of a manual run is read from the access token by <see cref="ICurrentUserService"/> and passed
/// to the dispatcher, which is the only way a wishlist-scoped job learns whose list to price. Nothing in the
/// request body can name a user.
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize(Policy = "AdminWithId")]
public sealed class JobsController(
    IJobRunLogQueryService query,
    JobCatalog catalog,
    JobSubmissionService submissions,
    ICurrentUserService currentUser) : ControllerBase
{
    private static readonly string[] KnownStatuses =
    [
        JobRunStatuses.Running,
        JobRunStatuses.Ok,
        JobRunStatuses.Failed
    ];

    /// <summary>
    /// Run history, paginated and optionally narrowed by job, status or trigger. The response keeps the
    /// shape the page already consumed; <c>filters</c> is additive.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<JobRunHistoryResponse>> GetAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? job = null,
        [FromQuery] string? status = null,
        [FromQuery] string? trigger = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStatus(status);

        var result = await query.QueryAsync(
            new JobRunQueryFilter(job, status, trigger, page, pageSize),
            cancellationToken);

        return Ok(new JobRunHistoryResponse(
            new JobRunSummaryResponse(
                result.Summary.Total,
                result.Summary.Running,
                result.Summary.Ok,
                result.Summary.Failed,
                result.Summary.LastStartedAt),
            result.Items.Select(ToListItem).ToList(),
            result.Page,
            result.PageSize,
            result.TotalPages,
            new JobRunFilterResponse(result.Filter.Job, result.Filter.Status, result.Filter.Trigger)));
    }

    /// <summary>
    /// Every registered job with its availability, its bounded options, whether it is running right now and
    /// its last result. Disabled jobs are listed with the reason instead of being hidden or offered.
    /// </summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<JobCatalogResponse>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var latest = await query.GetLatestByJobAsync(JobKeys.All, cancellationToken);
        var byJob = latest.ToDictionary(entry => entry.Job, StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        var items = catalog.All.Select(definition =>
        {
            byJob.TryGetValue(definition.Job, out var run);
            var isRunning = run is not null &&
                            run.Status == JobRunStatuses.Running &&
                            run.StartedAt >= now - JobRunTracker.AbandonedAfter;
            var isStale = run is not null &&
                          run.Status == JobRunStatuses.Running &&
                          run.StartedAt < now - JobRunTracker.AbandonedAfter;

            return new JobCatalogItemResponse(
                definition.Job,
                definition.DisplayName,
                definition.Description,
                definition.Enabled,
                definition.DisabledReason,
                definition.Schedule,
                isRunning,
                isStale,
                definition.Destructive,
                definition.RequiresConfirmation,
                definition.Options
                    .Select(JobOptionResponse.From)
                    .ToList(),
                run is null ? null : ToListItem(run));
        }).ToList();

        return Ok(new JobCatalogResponse(items, DateTime.UtcNow));
    }

    /// <summary>One run with its complete, sanitised details.</summary>
    [HttpGet("{jobRunId:long}")]
    public async Task<ActionResult<JobRunDetailResponse>> GetRunAsync(
        long jobRunId,
        CancellationToken cancellationToken)
    {
        var run = await query.GetAsync(jobRunId, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        return Ok(new JobRunDetailResponse(
            run.JobRunId,
            run.Job,
            run.Trigger,
            run.Status,
            run.StartedAt,
            run.FinishedAt,
            run.DurationMilliseconds,
            JobDetailsSanitizer.Sanitize(run.Details)));
    }

    /// <summary>
    /// Asks for one manual run. Answers <c>202</c> with the run id as soon as the row exists and the job is
    /// queued; the work happens in the hosted dispatcher, so a job that takes tens of minutes never holds
    /// this request open.
    ///
    /// Refusals are explicit: <c>404</c> unknown job, <c>400</c> invalid options or missing confirmation,
    /// <c>409</c> a run of that job is already in progress, <c>403</c> a personal job asked for without an
    /// authenticated requester. The rate limit is per administrator, because every accepted request spends a
    /// provider budget.
    /// </summary>
    [EnableRateLimiting("jobs-dispatch")]
    [HttpPost("{job}/run")]
    public async Task<ActionResult<JobRunQueuedResponse>> RunAsync(
        string job,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JobRunRequest? request,
        CancellationToken cancellationToken)
    {
        var outcome = await submissions.SubmitAsync(
            job,
            request?.Options,
            request?.Confirm ?? false,
            cancellationToken,
            // Server-side identity only: the authenticated caller, never a body field.
            currentUser.GetUserId());

        if (outcome.IsSuccess)
        {
            return Accepted(
                new JobRunQueuedResponse(
                    outcome.JobRunId!.Value,
                    outcome.Definition!.Job,
                    JobRunStatuses.Running,
                    outcome.RequestedAt ?? DateTime.UtcNow));
        }

        return outcome.Status switch
        {
            JobSubmitStatus.NotFound => Rejected(StatusCodes.Status404NotFound, "job_not_found", outcome.Message),
            JobSubmitStatus.Disabled => Rejected(StatusCodes.Status409Conflict, "job_disabled", outcome.Message),
            JobSubmitStatus.ConfirmationRequired => Rejected(
                StatusCodes.Status400BadRequest,
                "job_confirmation_required",
                outcome.Message),
            JobSubmitStatus.RequesterRequired => Rejected(
                StatusCodes.Status403Forbidden,
                "job_requester_required",
                outcome.Message),
            JobSubmitStatus.Invalid => Rejected(StatusCodes.Status400BadRequest, "job_options_invalid", outcome.Message),
            JobSubmitStatus.Duplicate => Rejected(
                StatusCodes.Status409Conflict,
                "job_already_running",
                outcome.Message),
            _ => Rejected(StatusCodes.Status409Conflict, "job_rejected", outcome.Message)
        };
    }

    private ActionResult Rejected(int statusCode, string code, string? message)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = "Ejecución rechazada",
            Detail = message ?? "No se pudo iniciar la ejecución.",
            Type = $"https://dealext.local/problems/{code}"
        };
        problem.Extensions["code"] = code;

        return StatusCode(statusCode, problem);
    }

    private static void ValidateStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        if (!KnownStatuses.Contains(status.Trim(), StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"El estado '{status}' no existe. Valores válidos: {string.Join(", ", KnownStatuses)}.",
                nameof(status));
        }
    }

    private static JobRunListItemResponse ToListItem(JobRunQueryItem item) =>
        new(
            item.JobRunId,
            item.Job,
            item.Trigger,
            item.Status,
            item.StartedAt,
            item.FinishedAt,
            item.DurationMilliseconds,
            JobDetailsSanitizer.Sanitize(item.Details));

    private static JobRunListItemResponse ToListItem(JobRunLatest item) =>
        new(
            item.JobRunId,
            item.Job,
            item.Trigger,
            item.Status,
            item.StartedAt,
            item.FinishedAt,
            item.DurationMilliseconds,
            JobDetailsSanitizer.Sanitize(item.Details));
}
