namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Explicit map from a Playnite platform display name to a catalog platform slug
/// (<c>docs/PLAN_CONSOLE.md</c> §7, bulk import).
///
/// <para>
/// It is a <em>suggestion</em> table, never an identity or ownership rule: the platform of a library row is
/// always the one the person picked in the commit decision. A display name that is not listed here produces
/// no suggestion — the entry comes back with <c>needsPlatform</c> and the dialog asks for the platform
/// instead of guessing one. That is why an unmapped or misspelled value is safe: it degrades to "ask", it
/// never degrades to a wrong slug.
/// </para>
///
/// <para>
/// PC platforms are deliberately absent: mapping them would suggest a store key to a console import, and the
/// commit refuses the eight PC stores (<see cref="StoreKeys.IsKnown"/>) outright.
/// </para>
/// </summary>
public static class ConsolePlatformCatalog
{
    /// <summary>Allowed slug plus its visible label, mirroring <c>PLATFORM_CATALOG_KEYS</c> / <c>STORE_LABELS</c> in <c>Deals.Web/lib/contracts/stores.ts</c>.</summary>
    private sealed record CatalogPlatform(string Slug, string DisplayName);

    private static readonly IReadOnlyDictionary<string, CatalogPlatform> ByDisplay =
        new Dictionary<string, CatalogPlatform>(StringComparer.OrdinalIgnoreCase)
        {
            ["nintendo switch"] = new("switch", "Nintendo Switch"),

            ["playstation 5"] = new("ps5", "PlayStation 5"),
            ["playstation 4"] = new("ps4", "PlayStation 4"),
            ["playstation 3"] = new("ps3", "PlayStation 3"),
            ["playstation 2"] = new("ps2", "PlayStation 2"),
            ["playstation"] = new("ps1", "PlayStation"),
            ["playstation 1"] = new("ps1", "PlayStation"),
            ["playstation vita"] = new("ps-vita", "PlayStation Vita"),
            ["ps vita"] = new("ps-vita", "PlayStation Vita"),
            ["playstation portable"] = new("psp", "PSP"),
            ["psp"] = new("psp", "PSP"),

            ["xbox one"] = new("xbox-one", "Xbox One"),
            ["xbox 360"] = new("xbox360", "Xbox 360"),
            ["xbox series x"] = new("series-x", "Xbox Series X"),
            ["xbox series s"] = new("series-s", "Xbox Series S"),

            ["nintendo 3ds"] = new("3ds", "Nintendo 3DS"),
            ["nintendo ds"] = new("ds", "Nintendo DS"),
            ["nintendo dsi"] = new("ds", "Nintendo DS"),
            ["nintendo wii u"] = new("wiiu", "Wii U"),
            ["wii u"] = new("wiiu", "Wii U"),
            ["nintendo wii"] = new("wii", "Wii"),
            ["wii"] = new("wii", "Wii"),
            ["nintendo gamecube"] = new("gamecube", "Nintendo GameCube"),
            ["gamecube"] = new("gamecube", "Nintendo GameCube"),
            ["nintendo 64"] = new("n64", "Nintendo 64"),
            ["super nintendo entertainment system"] = new("snes", "Super Nintendo"),
            ["super nintendo"] = new("snes", "Super Nintendo"),
            ["snes"] = new("snes", "Super Nintendo"),
            ["nintendo entertainment system"] = new("nes", "NES"),
            ["nes"] = new("nes", "NES"),

            ["sega dreamcast"] = new("dreamcast", "Sega Dreamcast"),
            ["dreamcast"] = new("dreamcast", "Sega Dreamcast")
        };

    /// <summary>
    /// Catalog suggestions for a Playnite platform list: known display names mapped to their slug, first
    /// occurrence per slug wins, order preserved. Empty when nothing can be suggested — which is exactly
    /// what <c>needsPlatform</c> reports.
    /// </summary>
    public static IReadOnlyList<ConsoleImportPlatformSuggestion> Suggest(IReadOnlyList<string>? platforms)
    {
        if (platforms is null || platforms.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var suggestions = new List<ConsoleImportPlatformSuggestion>();

        foreach (var raw in platforms)
        {
            var display = raw?.Trim();
            if (string.IsNullOrEmpty(display) || !ByDisplay.TryGetValue(display, out var platform))
            {
                continue;
            }

            if (!seen.Add(platform.Slug))
            {
                continue;
            }

            suggestions.Add(new ConsoleImportPlatformSuggestion(platform.Slug, platform.DisplayName, display));
        }

        return suggestions;
    }
}
