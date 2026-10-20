using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Read-side sanitiser of <c>job_runs.details</c>. The row is written by the jobs themselves and is meant to
/// hold counters and reasons, but a provider exception message is free text: an HTTP client failure can
/// embed the request URL (and gg.deals carries its API key as a query parameter), and a database failure can
/// embed a connection string.
///
/// Three rules, applied to every string value:
/// <list type="number">
/// <item>a property whose name is a credential (<c>password</c>, <c>token</c>, <c>apiKey</c>, …) never
/// renders its value, whatever its JSON type;</item>
/// <item>inside free text, a bearer token, a <c>key=…</c>/<c>password=…</c> pair or a URL userinfo section is
/// replaced by a redaction marker, keeping the rest of the diagnosis readable;</item>
/// <item>a runaway string is truncated, so one payload cannot flood the table or the page.</item>
/// </list>
/// The shape of the document is preserved: the UI renders the same JSON it would have rendered before, with
/// the secrets replaced.
///
/// The transform writes a fresh document instead of rebuilding a <c>JsonNode</c> tree out of the source
/// nodes. Reusing a parsed node in a second parent throws "The node already has a parent", and since every
/// real execution carries numeric and boolean counters, that was not a corner case: it turned every Jobs GET
/// into a 500. Writing through <see cref="Utf8JsonWriter"/> also means the source element is never mutated,
/// numbers keep their exact text, and booleans and nulls keep their JSON types.
/// </summary>
public static class JobDetailsSanitizer
{
    /// <summary>Ceiling of a single string value in the admin detail view.</summary>
    public const int MaxStringLength = 2_000;

    private const string Redacted = "[redactado]";

    /// <summary>Runs first: it is the only rule that knows how far a bearer token extends.</summary>
    private static readonly Regex BearerPattern = new(
        @"\b(?<scheme>bearer)\s+[^\s,;""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>key=…</c>, <c>api_key: …</c>, <c>password=…</c>, <c>authorization: …</c>.</summary>
    private static readonly Regex CredentialPairPattern = new(
        @"\b(?<name>key|apikey|api_key|api-key|x-api-key|token|access_token|refresh_token|password|pwd|secret|authorization|auth)\b(?<separator>\s*[=:]\s*)(?<value>[^\s&;,""'}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>scheme://user:password@host</c>.</summary>
    private static readonly Regex UrlUserInfoPattern = new(
        @"://[^/\s:@]+:[^/\s@]+@",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apiKey", "api_key", "api-key", "x-api-key", "apikey",
        "token", "accessToken", "access_token", "refreshToken", "refresh_token",
        "auth", "authorization", "password", "pwd", "secret", "clientSecret", "client_secret",
        "banxicoToken", "apiToken", "authToken", "connectionString"
    };

    public static JsonElement? Sanitize(JsonElement? details)
    {
        if (details is null || details.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Write(writer, details.Value);
            }

            // Clone: the caller keeps the element after this document is disposed.
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Unparseable detail: the row still exists, its payload is simply not readable JSON.
            return null;
        }
    }

    /// <summary>Redacts credentials and truncates one free-text value.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = BearerPattern.Replace(value, "${scheme} " + Redacted);
        redacted = CredentialPairPattern.Replace(redacted, "${name}${separator}" + Redacted);
        redacted = UrlUserInfoPattern.Replace(redacted, "://" + Redacted + "@");

        return redacted.Length <= MaxStringLength ? redacted : redacted[..MaxStringLength] + "…";
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    // A credential-named property never renders its value, whatever the JSON type is: a
                    // numeric token is as much a secret as a string one. Structures fall through so their
                    // own children decide.
                    if (SensitiveNames.Contains(property.Name) &&
                        property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    {
                        writer.WriteStringValue(Redacted);
                        continue;
                    }

                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                writer.WriteStringValue(Redact(element.GetString() ?? string.Empty));
                return;

            case JsonValueKind.Number:
                // Raw text keeps the exact number the job recorded (integers, decimals, exponents).
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                return;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;

            default:
                writer.WriteNullValue();
                return;
        }
    }
}
