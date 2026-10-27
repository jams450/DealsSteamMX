using System.Text.Json.Serialization;

namespace Deals.BusinessLogic.Models.Diagnostics;

/// <summary>
/// The closed provider/phase vocabulary of the sync diagnosis (spec S3/S5). Every token here is a machine code:
/// the admin page renders the Spanish label, the persisted document keeps the code so a support message can quote
/// it. Nothing outside this set may reach <c>job_runs.details</c>, and the read side rejects anything else instead
/// of trusting the stored document.
/// </summary>
public enum SyncDiagnosticProvider
{
    Steam,
    Itad,
    ItadIdentity,
    ItadBundles,
    GgDeals,
    Epic,
    Microsoft
}

/// <summary>What a phase really did. <see cref="NoMatch"/> is an authoritative "there is nothing here", never a failure.</summary>
public enum SyncDiagnosticOutcome
{
    Refreshed,
    NoMatch,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>
/// Why a phase ended the way it did. Deliberately about the phase, not about the exception: no message, no URL, no
/// payload and no stack is ever part of it. <see cref="Cancelled"/> means the caller (a shutdown, a request that
/// went away) cancelled the work — it is never a provider failure.
/// </summary>
public enum SyncDiagnosticReason
{
    Refreshed,
    HttpError,
    TransportError,
    Timeout,
    ParseError,
    NoIdentity,
    TitleFallbackDisabled,
    StoreNoOffer,
    NotComparable,
    GateSkipped,
    GateDeclined,
    Cancelled,
    UnknownFailure
}

/// <summary>
/// One provider phase, as the job observed it. Everything on it is either a bounded number, a closed token or the
/// CLR type name of the exception — never the exception object, its message, the request URL or the response body.
/// </summary>
public sealed record SyncDiagnosticEvent(
    [property: JsonPropertyName("appId")] int? AppId,
    [property: JsonPropertyName("gameId")] long? GameId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("httpStatus")] int? HttpStatus,
    [property: JsonPropertyName("errorType")] string? ErrorType,
    [property: JsonPropertyName("elapsedMs")] long? ElapsedMs);

/// <summary>
/// One aggregate line. The counts here cover <em>every</em> recorded event, not the bounded slice that travels in
/// <see cref="SyncDiagnosticsSnapshot.Events"/>: that is what makes the summary readable after 600 games.
/// </summary>
public sealed record SyncDiagnosticSummaryRow(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("httpStatus")] int? HttpStatus,
    [property: JsonPropertyName("count")] int Count);

/// <summary>
/// The additive diagnostics document of one run (spec S5). It hangs off <c>job_runs.details</c> either at
/// <c>details.diagnostics</c> (a scheduled cycle, whose details <em>are</em> the executor's result) or at
/// <c>details.result.diagnostics</c> (a manual run, which the runner wraps with its provenance).
/// </summary>
public sealed record SyncDiagnosticsSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("totalEvents")] int TotalEvents,
    [property: JsonPropertyName("shownEvents")] int ShownEvents,
    [property: JsonPropertyName("truncatedEvents")] int TruncatedEvents,
    [property: JsonPropertyName("summary")] IReadOnlyList<SyncDiagnosticSummaryRow> Summary,
    [property: JsonPropertyName("events")] IReadOnlyList<SyncDiagnosticEvent> Events);

/// <summary>
/// The frozen vocabulary and the two bounds of the document. Public because the read-side validator and the offline
/// checks must agree with the writer <em>by construction</em>, not by a copied list of strings.
/// </summary>
public static class SyncDiagnosticsContract
{
    /// <summary>Version of the document. A stored document under any other version is historical, not readable.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// How many per-game events travel in the document. The aggregate summary is complete regardless: 600 games
    /// produce 600 counted events and 200 detailed ones, and both numbers are reported.
    /// </summary>
    public const int MaxEvents = 200;

    /// <summary>
    /// Longest <c>errorType</c> accepted, matching the page's own 64-character bound. A CLR type name is far shorter;
    /// anything longer is dropped rather than truncated, so a fragment of a message can never pass as a type.
    /// </summary>
    public const int MaxErrorTypeLength = 64;

    /// <summary>
    /// Ceiling of distinct aggregate lines. The admin page caps the summary at this number, so the writer must never
    /// produce more: the group key is (provider, outcome, reason) over closed vocabularies (7 × 5 × 13 = 455).
    /// </summary>
    public const int MaxSummaryGroups = 512;

    /// <summary>The property name that carries the document, matched at any depth of <c>job_runs.details</c>.</summary>
    public const string DetailsProperty = "diagnostics";

    public static string Token(this SyncDiagnosticProvider provider) => provider switch
    {
        SyncDiagnosticProvider.Steam => "steam",
        SyncDiagnosticProvider.Itad => "itad",
        SyncDiagnosticProvider.ItadIdentity => "itad-identity",
        SyncDiagnosticProvider.ItadBundles => "itad-bundles",
        SyncDiagnosticProvider.GgDeals => "ggdeals",
        SyncDiagnosticProvider.Epic => "epic",
        SyncDiagnosticProvider.Microsoft => "microsoft",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown diagnostic provider.")
    };

    public static string Token(this SyncDiagnosticOutcome outcome) => outcome switch
    {
        SyncDiagnosticOutcome.Refreshed => "refreshed",
        SyncDiagnosticOutcome.NoMatch => "no_match",
        SyncDiagnosticOutcome.Failed => "failed",
        SyncDiagnosticOutcome.Skipped => "skipped",
        SyncDiagnosticOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown diagnostic outcome.")
    };

    public static string Token(this SyncDiagnosticReason reason) => reason switch
    {
        SyncDiagnosticReason.Refreshed => "refreshed",
        SyncDiagnosticReason.HttpError => "http_error",
        SyncDiagnosticReason.TransportError => "transport_error",
        SyncDiagnosticReason.Timeout => "timeout",
        SyncDiagnosticReason.ParseError => "parse_error",
        SyncDiagnosticReason.NoIdentity => "no_identity",
        SyncDiagnosticReason.TitleFallbackDisabled => "title_fallback_disabled",
        SyncDiagnosticReason.StoreNoOffer => "store_no_offer",
        SyncDiagnosticReason.NotComparable => "not_comparable",
        SyncDiagnosticReason.GateSkipped => "gate_skipped",
        SyncDiagnosticReason.GateDeclined => "gate_declined",
        SyncDiagnosticReason.Cancelled => "cancelled",
        SyncDiagnosticReason.UnknownFailure => "unknown_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown diagnostic reason.")
    };

    /// <summary>The phase name <see cref="SteamGameService"/> logs and measures, expressed as its provider token.</summary>
    public static SyncDiagnosticProvider? ProviderForPhase(string? phase) => phase switch
    {
        "steam" => SyncDiagnosticProvider.Steam,
        "itad" => SyncDiagnosticProvider.Itad,
        "itad-identity" => SyncDiagnosticProvider.ItadIdentity,
        "itad-bundles" => SyncDiagnosticProvider.ItadBundles,
        "ggdeals" => SyncDiagnosticProvider.GgDeals,
        "epic" => SyncDiagnosticProvider.Epic,
        "microsoft" => SyncDiagnosticProvider.Microsoft,
        _ => null
    };

    public static bool IsKnownProvider(string? token) =>
        ProviderTokens.Contains(token ?? string.Empty);

    public static bool IsKnownOutcome(string? token) =>
        OutcomeTokens.Contains(token ?? string.Empty);

    public static bool IsKnownReason(string? token) =>
        ReasonTokens.Contains(token ?? string.Empty);

    /// <summary>Canonical (lower-case) token for a stored one, matching case-insensitively. Null when unknown.</summary>
    public static string? Normalize(IReadOnlySet<string> known, string? token) =>
        token is null ? null : known.FirstOrDefault(candidate =>
            string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlySet<string> ProviderTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "steam", "itad", "itad-identity", "itad-bundles", "ggdeals", "epic", "microsoft"
    };

    public static IReadOnlySet<string> OutcomeTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "refreshed", "no_match", "failed", "skipped", "cancelled"
    };

    public static IReadOnlySet<string> ReasonTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "refreshed", "http_error", "transport_error", "timeout", "parse_error", "no_identity",
        "title_fallback_disabled", "store_no_offer", "not_comparable", "gate_skipped", "gate_declined",
        "cancelled", "unknown_failure"
    };

    /// <summary>
    /// A CLR/exception type name as it may be persisted: dotted, no whitespace, no URL and no punctuation that a
    /// message would bring. Anything else is dropped rather than rendered.
    /// </summary>
    public static string? SafeErrorType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName) || typeName.Length > MaxErrorTypeLength)
        {
            return null;
        }

        foreach (var character in typeName)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '+' or '`'))
            {
                return null;
            }
        }

        return char.IsAsciiLetter(typeName[0]) || typeName[0] == '_' ? typeName : null;
    }
}
