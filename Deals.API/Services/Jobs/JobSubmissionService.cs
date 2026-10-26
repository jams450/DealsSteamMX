using System.Text.Json;
using Deals.Models.Entities;

namespace Deals.API.Services.Jobs;

public enum JobSubmitStatus
{
    /// <summary>The run was recorded and handed to the dispatcher; the caller gets its id.</summary>
    Submitted,

    NotFound,
    Disabled,
    Invalid,
    ConfirmationRequired,

    /// <summary>
    /// The job only acts on one person's data and no authenticated requester was supplied. It is a refusal, not a
    /// degradation: nothing was written and no provider budget was spent.
    /// </summary>
    RequesterRequired,
    Duplicate
}

/// <summary>
/// Result of asking for one manual run. It carries the catalog entry even when the request was refused, so
/// the API can explain a rejection in terms of the job the operator selected.
/// </summary>
public sealed record JobSubmitOutcome(
    JobSubmitStatus Status,
    JobDefinition? Definition,
    string? Message = null,
    long? JobRunId = null,
    DateTime? RequestedAt = null)
{
    public bool IsSuccess => Status == JobSubmitStatus.Submitted;
}

/// <summary>
/// Accepts one manual run request. The HTTP request returns as soon as the run row exists and the job is in
/// the dispatcher queue; the work itself happens in <see cref="JobRunDispatchService"/>, which is why a
/// wishlist refresh that takes tens of minutes cannot hold a request open.
///
/// The order of the checks is the contract:
/// <list type="number">
/// <item>unknown job, disabled job and invalid options are refused before anything is written;</item>
/// <item>a job that acts on one person's data is refused when the request carries no authenticated requester
/// (the id comes from the access token, never from the payload), so it can never fall back to a wider scope;</item>
/// <item>a live run of the same job — in this process or recorded by another instance — is a conflict;</item>
/// <item>the job is reserved in the queue <em>before</em> the row is created, so two simultaneous requests
/// cannot both reach the insert;</item>
/// <item>if publishing then fails, the row that already exists is closed as failed with the reason instead
/// of being left <c>running</c> forever, and the reservation is freed.</item>
/// </list>
/// </summary>
public sealed class JobSubmissionService(
    JobCatalog catalog,
    IJobRunQueue queue,
    IJobRunTracker tracker,
    ILogger<JobSubmissionService> logger)
{
    public async Task<JobSubmitOutcome> SubmitAsync(
        string? job,
        JsonElement? options,
        bool confirm,
        CancellationToken cancellationToken,
        int? requestedByUserId = null)
    {
        var definition = catalog.Find(job);
        if (definition is null)
        {
            return new JobSubmitOutcome(
                JobSubmitStatus.NotFound,
                Definition: null,
                Message: "El job solicitado no existe.");
        }

        // A disabled provider is reported as such. Starting it would consume budgets the operator
        // explicitly switched off, and reporting success on a job that exits immediately would be worse.
        if (!definition.Enabled)
        {
            return new JobSubmitOutcome(
                JobSubmitStatus.Disabled,
                definition,
                $"Este job está deshabilitado: {definition.DisabledReason ?? "su configuración lo desactiva."}");
        }

        var parsed = JobOptionParser.Parse(definition, options, confirm);
        if (!parsed.Succeeded)
        {
            return new JobSubmitOutcome(
                parsed.RequiresConfirmation ? JobSubmitStatus.ConfirmationRequired : JobSubmitStatus.Invalid,
                definition,
                parsed.Error);
        }

        // A job that only acts on one person's data needs that person, and the id must come from the access
        // token the controller already validated — never from the payload. Without it the run is refused here,
        // before a row or a queue reservation exists, so it cannot fall back to a catalogue-wide pass.
        if (definition.RequiresRequester && requestedByUserId is not > 0)
        {
            return new JobSubmitOutcome(
                JobSubmitStatus.RequesterRequired,
                definition,
                "Este job solo puede ejecutarse desde una sesión autenticada: no hay un usuario solicitante " +
                "al que limitar el lote.");
        }

        if (await tracker.HasRunningAsync(definition.Job, cancellationToken))
        {
            return new JobSubmitOutcome(
                JobSubmitStatus.Duplicate,
                definition,
                "Ya hay una ejecución en curso para este job. Espera a que termine para volver a lanzarlo.");
        }

        if (!queue.TryReserve(definition.Job))
        {
            return new JobSubmitOutcome(
                JobSubmitStatus.Duplicate,
                definition,
                "Ya hay una ejecución en curso o en cola para este job.");
        }

        long jobRunId;
        try
        {
            // The requester is recorded with the row before the work starts, so an interrupted run still says
            // whose list it was going to refresh.
            jobRunId = await tracker.StartAsync(
                definition.Job,
                cancellationToken,
                definition.RequiresRequester && requestedByUserId is { } requester
                    ? new { requestedBy = requester }
                    : null);
        }
        catch
        {
            // Half a submission is not a submission: free the job so the operator can retry.
            queue.Release(definition.Job);
            throw;
        }

        var requestedAt = DateTime.UtcNow;
        if (!queue.TryPublish(new JobQueueItem(
                definition.Job,
                jobRunId,
                parsed.Options,
                definition.RequiresRequester ? requestedByUserId : null)))
        {
            queue.Release(definition.Job);
            await TryFinishAsync(
                jobRunId,
                JobRunStatuses.Failed,
                new { reason = "queue_full", message = "La cola de ejecuciones manuales está llena." },
                cancellationToken);
            return new JobSubmitOutcome(
                JobSubmitStatus.Duplicate,
                definition,
                "La cola de ejecuciones manuales está llena. Intenta de nuevo cuando termine la ejecución en curso.");
        }

        logger.LogInformation(
            "[jobs.manual] queued job={Job} jobRunId={JobRunId} requestedBy={RequestedBy} options={Options}",
            definition.Job,
            jobRunId,
            requestedByUserId,
            string.Join(',', parsed.Options.ToDetails().Select(pair => $"{pair.Key}={pair.Value}")));

        return new JobSubmitOutcome(JobSubmitStatus.Submitted, definition, null, jobRunId, requestedAt);
    }

    private async Task TryFinishAsync(long jobRunId, string status, object details, CancellationToken cancellationToken)
    {
        try
        {
            await tracker.FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            // The row is the record, but a failed log write must not turn the refusal into a 500.
            logger.LogWarning(
                exception,
                "[jobs.manual] could not close the refused run {JobRunId}",
                jobRunId);
        }
    }
}
