namespace Deals.API.Models.Library;

using Deals.API.Models.Reviews;

/// <summary>
/// One entry of a Playnite library export. Properties keep the exact JSON names the exporter emits;
/// extra fields are ignored by design so exports from newer Playnite builds keep working.
/// <see cref="Added"/> stays a string because Playnite serializes .NET dates as
/// <c>/Date(&lt;epoch-milliseconds&gt;)/</c>, which System.Text.Json cannot bind to <see cref="DateTime"/>.
/// </summary>
public sealed class PlayniteLibraryEntry
{
    public string? GameId { get; set; }
    public string? PluginId { get; set; }
    public string? Source { get; set; }
    public string? Name { get; set; }
    public bool? IsInstalled { get; set; }
    public string? Added { get; set; }
}

/// <summary>
/// Result of a Playnite import. <see cref="Unresolved"/> is always 0 in this phase (the canonical
/// catalog does not exist yet); <see cref="ByStore"/> counts accepted entries per canonical store.
/// </summary>
public sealed record LibraryImportResponse(
    int Imported,
    int Updated,
    int Unresolved,
    int UnsupportedSource,
    IReadOnlyDictionary<string, int> ByStore);

/// <summary>
/// One accepted library row. PluginId is never exposed. The trailing fields are the read-only price
/// binding: <c>PriceState</c> is <c>exact | title_candidate | none | subscription</c>, and the amounts
/// are MXN minor units (null when there is no converted price). No field implies ownership.
/// <see cref="GameId"/> is the canonical link (null when the row has no identity) and <see cref="Review"/>
/// is the caller's review for this exact <c>(gameId, platform)</c>, or null when there is none.
/// </summary>
public sealed record LibraryItemResponse(
    long UserLibraryId,
    string Store,
    string StoreGameId,
    string Title,
    string State,
    bool? IsInstalled,
    DateTime? AddedAt,
    DateTime ImportedAt,
    string PriceState,
    string? BindingSource,
    int? SteamAppId,
    int? BestOfficialMinor,
    int? BestKeyshopMinor,
    int? HistoryLowMinor,
    int? BasePriceMinor,
    string? BaseCurrency,
    long? GameId,
    ReviewResponse? Review);
