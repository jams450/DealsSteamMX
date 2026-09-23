using System.Text.RegularExpressions;

namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Canonical store keys, the single source of truth shared by the library import and reviews.
/// Reviews use the same vocabulary as <c>user_library.store</c>, so a review and a library row join by
/// plain text without translation.
/// </summary>
public static class StoreKeys
{
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Gog = "gog";
    public const string Xbox = "xbox";
    public const string Amazon = "amazon";
    public const string Ubisoft = "ubisoft";
    public const string Humble = "humble";
    public const string Battlenet = "battlenet";

    private static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Steam, Epic, Gog, Xbox, Amazon, Ubisoft, Humble, Battlenet
    };

    /// <summary>True when the value is one of the eight canonical store keys (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value);

    // Open platform slug (switch, psp, nds, snes, ...): consoles enter the vocabulary without code
    // changes (docs/PLAN_CONSOLE.md §3). 2..32 chars, the width of user_library.store,
    // game_reviews.platform and game_external_ids.namespace (all VARCHAR(32)). Mirror of
    // `normalizeStore` in Deals.Web/lib/contracts/stores.ts.
    private static readonly Regex PlatformSlug = new(
        "^[a-z0-9][a-z0-9._-]{1,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Canonical vocabulary for <c>user_library.store</c> and <c>game_reviews.platform</c>: a known
    /// store key or any open platform slug, trimmed and lowercased. Null when it is neither; callers
    /// treat null as a 400 instead of writing an unvalidated platform.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return null;
        return All.Contains(text) || PlatformSlug.IsMatch(text) ? text : null;
    }
}
