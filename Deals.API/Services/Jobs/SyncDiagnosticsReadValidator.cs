using System.Text.Json;
using Deals.BusinessLogic.Models.Diagnostics;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Schema-aware read path of the sync diagnosis (spec S5/S7).
///
/// <para>
/// The detail row is written by us but read as untrusted: an older deployment, a hand-edited row or a future version
/// may have put anything under <c>diagnostics</c>. So the subtree is not copied, it is <em>rebuilt</em> from the
/// frozen contract: only the documented fields survive, every token must belong to its closed set, the exception type
/// must look like a type name (never a URL or a message) and every number must be the kind of number it claims to be.
/// Unknown extra fields are discarded; an unknown schema version drops the whole document, because a historical
/// report that cannot be understood must read as "no diagnostics" and never as "nothing failed".
/// </para>
///
/// <para>
/// The aggregate lines are grouped by (provider, outcome, reason) and carry <c>httpStatus: null</c>; a stored non-null
/// status is tolerated because the field is nullable in the contract, but it is never invented for a line that has none.
///
/// The counts are recomputed honestly. The stored <c>shownEvents</c> is not trusted: a stored list longer than the
/// collector's cap is cut here, an event that fails validation is dropped, and <c>shownEvents</c>/<c>truncatedEvents</c>
/// are recomputed so the page can never claim to display events it was not given. The summary is <em>not</em>
/// recomputed from the events: it is the complete aggregate over every event of the run, which is exactly why it
/// survives the cap. A document whose summary disagrees with its own total is not trustworthy and is dropped whole.
/// </para>
///
/// <para>
/// This is the schema-aware bound the generic sanitiser must not fake: the generic transform truncates strings and
/// redacts credentials, but it never cuts an array, so the 200 events a job reported are the 200 the page sees —
/// with counters that say so.
/// </para>
/// </summary>
public static class SyncDiagnosticsReadValidator
{
    /// <summary>True for the property that carries the document, at any depth of the job details.</summary>
    public static bool IsDiagnosticsProperty(string? name) =>
        string.Equals(name, SyncDiagnosticsContract.DetailsProperty, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates and normalises one stored document. False means "render no diagnostics at all": the caller omits the
    /// property instead of rendering a partial or unreadable report.
    /// </summary>
    public static bool TryNormalize(JsonElement value, out JsonElement normalized)
    {
        normalized = default;

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(value, "schemaVersion", out var schema) ||
            !TryReadInt(schema, out var schemaVersion) ||
            schemaVersion != SyncDiagnosticsContract.SchemaVersion)
        {
            // Historical or unreadable schema: no diagnosis, never a zero-failure claim.
            return false;
        }

        if (!TryGetProperty(value, "totalEvents", out var totalElement) ||
            !TryReadInt(totalElement, out var totalEvents) ||
            totalEvents < 0)
        {
            return false;
        }

        if (!TryGetProperty(value, "summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (!TryGetProperty(value, "events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var summaryRows = new List<SummaryRow>();
        foreach (var row in summary.EnumerateArray())
        {
            // The group key is (provider, outcome, reason) over closed vocabularies, so the writer can never exceed
            // the page's ceiling. A stored document that does is not ours: it is dropped rather than rendered, because
            // cutting aggregate lines would silently lose the counts they carry.
            if (summaryRows.Count >= SyncDiagnosticsContract.MaxSummaryGroups)
            {
                return false;
            }

            if (TryReadSummaryRow(row, out var parsed))
            {
                summaryRows.Add(parsed);
            }
        }

        var eventRows = new List<EventRow>();
        foreach (var row in events.EnumerateArray())
        {
            if (eventRows.Count >= SyncDiagnosticsContract.MaxEvents)
            {
                break;
            }

            if (TryReadEventRow(row, out var parsed))
            {
                eventRows.Add(parsed);
            }
        }

        // The aggregate must agree with its own total. If it does not, nothing in the document can be trusted — a
        // report that has already lost events cannot say how many it lost.
        var counted = 0L;
        foreach (var row in summaryRows)
        {
            counted += row.Count;
        }

        if (counted != totalEvents || eventRows.Count > totalEvents)
        {
            return false;
        }

        var shown = eventRows.Count;
        var truncated = totalEvents - shown;

        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SyncDiagnosticsContract.SchemaVersion);
                writer.WriteNumber("totalEvents", totalEvents);
                writer.WriteNumber("shownEvents", shown);
                writer.WriteNumber("truncatedEvents", truncated);

                writer.WriteStartArray("summary");
                foreach (var row in summaryRows)
                {
                    writer.WriteStartObject();
                    writer.WriteString("provider", row.Provider);
                    writer.WriteString("outcome", row.Outcome);
                    writer.WriteString("reason", row.Reason);
                    WriteNullableNumber(writer, "httpStatus", row.HttpStatus);
                    writer.WriteNumber("count", row.Count);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteStartArray("events");
                foreach (var row in eventRows)
                {
                    writer.WriteStartObject();
                    WriteNullableNumber(writer, "appId", row.AppId);
                    WriteNullableNumber(writer, "gameId", row.GameId);
                    writer.WriteString("provider", row.Provider);
                    writer.WriteString("outcome", row.Outcome);
                    writer.WriteString("reason", row.Reason);
                    WriteNullableNumber(writer, "httpStatus", row.HttpStatus);
                    if (row.ErrorType is null)
                    {
                        writer.WriteNull("errorType");
                    }
                    else
                    {
                        writer.WriteString("errorType", row.ErrorType);
                    }

                    WriteNullableNumber(writer, "elapsedMs", row.ElapsedMs);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            normalized = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadSummaryRow(JsonElement row, out SummaryRow parsed)
    {
        parsed = default!;
        if (row.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadToken(row, "provider", SyncDiagnosticsContract.ProviderTokens, out var provider) ||
            !TryReadToken(row, "outcome", SyncDiagnosticsContract.OutcomeTokens, out var outcome) ||
            !TryReadToken(row, "reason", SyncDiagnosticsContract.ReasonTokens, out var reason))
        {
            return false;
        }

        if (!TryGetProperty(row, "count", out var countElement) ||
            !TryReadInt(countElement, out var count) ||
            count < 0)
        {
            return false;
        }

        if (!TryReadStatus(row, out var status))
        {
            return false;
        }

        parsed = new SummaryRow(provider, outcome, reason, status, count);
        return true;
    }

    private static bool TryReadEventRow(JsonElement row, out EventRow parsed)
    {
        parsed = default!;

        if (row.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadToken(row, "provider", SyncDiagnosticsContract.ProviderTokens, out var provider) ||
            !TryReadToken(row, "outcome", SyncDiagnosticsContract.OutcomeTokens, out var outcome) ||
            !TryReadToken(row, "reason", SyncDiagnosticsContract.ReasonTokens, out var reason))
        {
            return false;
        }

        if (!TryReadOptionalInt(row, "appId", out var appId) ||
            !TryReadOptionalLong(row, "gameId", out var gameId) ||
            !TryReadOptionalLong(row, "elapsedMs", out var elapsedMs))
        {
            return false;
        }

        if ((appId is not null && appId <= 0) || (gameId is not null && gameId <= 0) ||
            (elapsedMs is not null && elapsedMs < 0))
        {
            return false;
        }

        if (!TryReadStatus(row, out var status) || !TryReadErrorType(row, out var errorType))
        {
            return false;
        }

        parsed = new EventRow(appId, gameId, provider, outcome, reason, status, errorType, elapsedMs);
        return true;
    }

    private static bool TryReadStatus(JsonElement row, out int? status)
    {
        status = null;
        if (!TryGetProperty(row, "httpStatus", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryReadInt(element, out var code) || code is < 100 or > 599)
        {
            return false;
        }

        status = code;
        return true;
    }

    private static bool TryReadErrorType(JsonElement row, out string? errorType)
    {
        errorType = null;
        if (!TryGetProperty(row, "errorType", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // Not a bounded string: a type name. Anything that is not one (a message, a URL, a payload) fails here and
        // the event is dropped instead of being rendered.
        errorType = SyncDiagnosticsContract.SafeErrorType(element.GetString());
        return errorType is not null;
    }

    private static bool TryReadToken(
        JsonElement row,
        string name,
        IReadOnlySet<string> known,
        out string token)
    {
        token = string.Empty;
        if (!TryGetProperty(row, name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (SyncDiagnosticsContract.Normalize(known, element.GetString()) is not { } normalized)
        {
            return false;
        }

        token = normalized;
        return true;
    }

    private static bool TryReadOptionalInt(JsonElement row, string name, out int? value)
    {
        value = null;
        if (!TryGetProperty(row, name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryReadInt(element, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadOptionalLong(JsonElement row, string name, out long? value)
    {
        value = null;
        if (!TryGetProperty(row, name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            // Property names are tolerated in either casing (the contract says so on the page side too); the tokens
            // inside them are not free text, so they are validated against their closed set.
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteNumber(name, value.Value);
    }

    private sealed record SummaryRow(string Provider, string Outcome, string Reason, int? HttpStatus, int Count);

    private sealed record EventRow(
        int? AppId,
        long? GameId,
        string Provider,
        string Outcome,
        string Reason,
        int? HttpStatus,
        string? ErrorType,
        long? ElapsedMs);
}
