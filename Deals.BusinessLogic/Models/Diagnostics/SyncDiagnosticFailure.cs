using System.Net.Sockets;
using System.Text.Json;

namespace Deals.BusinessLogic.Models.Diagnostics;

/// <summary>
/// What one thrown exception means for a provider phase: a closed reason, the HTTP status when the exception really
/// carries one, and the exception's CLR type name. Never the message, the stack, the URL or the body.
/// </summary>
public sealed record SyncDiagnosticFailureInfo(
    SyncDiagnosticOutcome Outcome,
    SyncDiagnosticReason Reason,
    int? HttpStatus,
    string? ErrorType);

/// <summary>
/// The one place that turns a provider exception into safe diagnostic fields (spec S3).
///
/// <para>
/// A previous audit claimed <c>HttpRequestException.StatusCode</c> was never available, which would have made
/// "429 vs 403 vs 5xx" undecidable. That claim is wrong for this repository and the truth is narrower: the clients
/// that construct the exception with the response (<c>SteamStoreClient</c>, both FX providers, <c>GgDealsClient</c>,
/// and — since this unit — the Epic and Microsoft clients) do carry it; a client that only builds a message leaves it
/// null. So the rule is: report the status <em>when the exception carries one</em> and report nothing when it does
/// not. A made-up status is worse than an absent one, and an internally retried 429 that finally answered never
/// becomes a failure at all: only the exception that ends the phase is seen here.
/// </para>
///
/// <para>
/// The classification is deliberately conservative about cancellation: an <see cref="OperationCanceledException"/>
/// whose token was cancelled means the caller went away, not that the provider failed, while the same exception with
/// a live token is the HttpClient timeout it almost always is.
/// </para>
/// </summary>
public static class SyncDiagnosticFailure
{
    public static SyncDiagnosticFailureInfo Classify(Exception? exception, CancellationToken cancellationToken)
    {
        if (exception is null)
        {
            return new SyncDiagnosticFailureInfo(
                SyncDiagnosticOutcome.Failed,
                SyncDiagnosticReason.UnknownFailure,
                null,
                null);
        }

        // Type names are safe by construction: an exception type is not user text. The contract still bounds the
        // accepted shape, so a pathological name is dropped instead of persisted.
        var errorType = SyncDiagnosticsContract.SafeErrorType(exception.GetType().Name);

        return exception switch
        {
            // A timeout surfaces as a cancellation the caller never asked for.
            OperationCanceledException when cancellationToken.IsCancellationRequested =>
                new(SyncDiagnosticOutcome.Cancelled, SyncDiagnosticReason.Cancelled, null, errorType),
            OperationCanceledException =>
                new(SyncDiagnosticOutcome.Failed, SyncDiagnosticReason.Timeout, null, errorType),
            TimeoutException =>
                new(SyncDiagnosticOutcome.Failed, SyncDiagnosticReason.Timeout, null, errorType),
            HttpRequestException http => new(
                SyncDiagnosticOutcome.Failed,
                StatusOf(http) is null ? SyncDiagnosticReason.TransportError : SyncDiagnosticReason.HttpError,
                StatusOf(http),
                errorType),
            JsonException =>
                new(SyncDiagnosticOutcome.Failed, SyncDiagnosticReason.ParseError, null, errorType),
            SocketException or IOException =>
                new(SyncDiagnosticOutcome.Failed, SyncDiagnosticReason.TransportError, null, errorType),
            _ => new(SyncDiagnosticOutcome.Failed, SyncDiagnosticReason.UnknownFailure, null, errorType)
        };
    }

    /// <summary>
    /// The status the exception carries, bounded to the values an HTTP response can have. <c>null</c> means the
    /// exception does not know it — never that the response had none.
    /// </summary>
    public static int? StatusOf(HttpRequestException exception)
    {
        var status = exception.StatusCode;
        if (status is null)
        {
            return null;
        }

        var code = (int)status.Value;
        return code is >= 100 and <= 599 ? code : null;
    }
}
