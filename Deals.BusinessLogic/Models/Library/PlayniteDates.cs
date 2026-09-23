using System.Globalization;
using System.Text.RegularExpressions;

namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Parses the two date shapes a Playnite export emits for a game timestamp: the .NET
/// <c>/Date(&lt;epoch-milliseconds&gt;)/</c> literal and an ISO timestamp. Always UTC, because
/// <c>user_library.added_at</c> is <c>timestamptz</c>.
///
/// <para>
/// One copy only: the Playnite import (<c>POST /api/library/import</c>) and the console export parser
/// (<c>POST /api/library/console-import/*</c>) read the same field, and a second parser is a second
/// interpretation of the same wire format.
/// </para>
/// </summary>
public static class PlayniteDates
{
    private static readonly Regex DotNetDatePattern = new(
        @"^/Date\((-?\d+)\)/$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <paramref name="value"/> as UTC. Null or blank returns null (the field is optional in the
    /// console contract); a non-empty value that is neither shape throws <see cref="ArgumentException"/>,
    /// which the API maps to 400.
    /// </summary>
    public static DateTime? Parse(string? value, string field = "added")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        var match = DotNetDatePattern.Match(text);
        if (match.Success)
        {
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                throw new ArgumentException($"{field} is not a valid Playnite date", field);
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }

        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed.UtcDateTime;
        }

        throw new ArgumentException(
            $"{field} must be an ISO timestamp or /Date(<epoch-milliseconds>)/",
            field);
    }
}
