namespace Deals.API.Models.Library;

using Deals.BusinessLogic.Models.Library;

/// <summary>
/// The Heroic library envelope, exactly as <c>dealext-library@1</c> declares it
/// (<c>odd/tasks/heroic-library-export.md</c>, L4): <c>{format, version, source, exportedAt, games}</c>.
/// Every property is nullable so a missing one is refused by
/// <see cref="HeroicLibraryExportRules"/> with a message naming it, instead of being bound to a default that
/// happens to look valid. Extra fields are ignored on purpose: the whitelist is what this type reads, so a
/// newer exporter adding art metadata cannot widen what the server stores.
///
/// <para>
/// This is not the Playnite contract and never shares its parser: Playnite rows are keyed by a plugin GUID
/// and carry a Playnite date, Heroic rows name one of two stores and carry no date at all. Two different
/// gates, so neither can silently accept the other's file.
/// </para>
/// </summary>
public sealed class HeroicLibraryExportEnvelope
{
    /// <summary>Must be <c>dealext-library</c>.</summary>
    public string? Format { get; set; }

    /// <summary>Must be <c>1</c>.</summary>
    public int? Version { get; set; }

    /// <summary>Must be <c>heroic</c>.</summary>
    public string? Source { get; set; }

    /// <summary>ISO 8601 UTC instant. A string, so the gate refuses an offset or junk with its own message.</summary>
    public string? ExportedAt { get; set; }

    /// <summary>One entry per exported game. Required and non-empty.</summary>
    public List<HeroicLibraryEntry?>? Games { get; set; }
}

/// <summary>
/// One exported game. <see cref="Store"/> is <c>epic</c> or <c>gog</c>; <see cref="StoreGameId"/> is the
/// exact Epic <c>app_name</c> the Playnite plugin also uses, or the numeric GOG product id. Ownership is not
/// a field here: the server writes <c>owned</c> for every row, because Heroic only lists entries of an owned
/// library. That is the user's own claim — the same one a Playnite upload carries — not a verified ownership
/// proof, and the file cannot pick a different state than the one the import implies.
/// </summary>
public sealed class HeroicLibraryEntry
{
    public string? Store { get; set; }

    public string? StoreGameId { get; set; }

    public string? Title { get; set; }

    /// <summary>Required. Nullable so a missing value is a 400 instead of a silent <c>false</c>.</summary>
    public bool? IsInstalled { get; set; }
}
