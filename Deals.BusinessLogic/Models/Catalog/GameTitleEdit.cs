namespace Deals.BusinessLogic.Models.Catalog;

/// <summary>
/// How a canonical title edit resolves its new value. The two modes never mix: the client sends one or the
/// other, and the API refuses a body that carries both (or neither).
/// </summary>
public enum GameTitleEditMode
{
    /// <summary>The admin typed the title. It is written trimmed and normalized, with no provider call.</summary>
    Manual,

    /// <summary>
    /// The title is read from IGDB by id, server-side. The id is additionally claimed as an
    /// <c>('igdb', id)</c> mapping, so the same game is not linked to the provider twice.
    /// </summary>
    Igdb
}

/// <summary>
/// A canonical title edit. <see cref="Title"/> is only read in <see cref="GameTitleEditMode.Manual"/> and
/// <see cref="IgdbId"/> only in <see cref="GameTitleEditMode.Igdb"/> — never both.
/// </summary>
public sealed record GameTitleEditCommand(GameTitleEditMode Mode, string? Title, long? IgdbId);

/// <summary>Source labels the API echoes back so the caller knows where the stored title came from.</summary>
public static class GameTitleSources
{
    /// <summary>The admin typed the title.</summary>
    public const string Manual = "manual";

    /// <summary>The title came from the IGDB lookup by id.</summary>
    public const string Igdb = "igdb";
}

/// <summary>
/// Outcome of a title edit. <see cref="Applied"/> false is a refusal (HTTP 409), never an exception:
/// nothing was written and <see cref="Reason"/> is a safe, human-readable explanation the UI can show.
/// On success the fields carry what the catalog now stores, so the caller paints the server value instead
/// of the one it sent.
/// </summary>
public sealed record GameTitleEditResult(
    bool Applied,
    string? Reason,
    long GameId,
    string Title,
    string NormalizedTitle,
    string Source,
    long? IgdbId)
{
    public static GameTitleEditResult Success(
        long gameId,
        string title,
        string normalizedTitle,
        string source,
        long? igdbId) =>
        new(true, null, gameId, title, normalizedTitle, source, igdbId);

    public static GameTitleEditResult Conflict(long gameId, string reason) =>
        new(false, reason, gameId, string.Empty, string.Empty, string.Empty, null);
}
