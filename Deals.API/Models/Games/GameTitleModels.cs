using Deals.BusinessLogic.Models.Catalog;

namespace Deals.API.Models.Games;

/// <summary>
/// Body of a canonical-title edit. Exactly one mode is accepted:
/// <c>{ "mode": "manual", "title": "..." }</c> or <c>{ "mode": "igdb", "igdbId": 123 }</c>. Sending both
/// (or neither) is a 400: the shape is discriminated, never merged — the API does not guess which source
/// the admin meant. A manual title is trimmed and capped at 512 chars; the IGDB title is always read
/// server-side from the id and is never sent by the client.
/// </summary>
public sealed class GameTitleRequest
{
    /// <summary><c>manual</c> or <c>igdb</c>; any other value is a 400.</summary>
    public string? Mode { get; set; }

    /// <summary>Required in <c>manual</c> mode, forbidden in <c>igdb</c> mode.</summary>
    public string? Title { get; set; }

    /// <summary>Required in <c>igdb</c> mode, forbidden in <c>manual</c> mode.</summary>
    public long? IgdbId { get; set; }

    /// <summary>
    /// Strictly maps the wire body to the business command. An ambiguous shape is an
    /// <see cref="ArgumentException"/>, which the global handler reports as 400 with nothing written.
    /// </summary>
    public GameTitleEditCommand ToCommand()
    {
        var mode = (Mode ?? string.Empty).Trim().ToLowerInvariant();
        switch (mode)
        {
            case "manual":
                if (IgdbId is not null)
                {
                    throw new ArgumentException("mode=manual no admite igdbId.", nameof(IgdbId));
                }

                return new GameTitleEditCommand(GameTitleEditMode.Manual, Title, null);

            case "igdb":
                if (Title is not null)
                {
                    throw new ArgumentException("mode=igdb no admite title.", nameof(Title));
                }

                if (IgdbId is null)
                {
                    throw new ArgumentException("mode=igdb requiere igdbId.", nameof(IgdbId));
                }

                return new GameTitleEditCommand(GameTitleEditMode.Igdb, null, IgdbId);

            default:
                throw new ArgumentException("mode debe ser 'manual' o 'igdb'.", nameof(Mode));
        }
    }
}

/// <summary>
/// 200 body of a title edit. Mirrors what the catalog now stores, so the grid paints the server value
/// instead of the one it sent. <see cref="Source"/> is <c>manual</c> or <c>igdb</c> and
/// <see cref="IgdbId"/> is present only in <c>igdb</c> mode.
/// </summary>
public sealed record GameTitleEditResponse(
    long GameId,
    string Title,
    string NormalizedTitle,
    string Source,
    long? IgdbId)
{
    public static GameTitleEditResponse From(GameTitleEditResult result) =>
        new(result.GameId, result.Title, result.NormalizedTitle, result.Source, result.IgdbId);
}

/// <summary>
/// 409 body of a refused title edit: <see cref="Applied"/> is false and nothing was written.
/// <see cref="Reason"/> is a safe explanation the UI can show as-is; it never carries provider payloads,
/// URLs, external ids or credentials.
/// </summary>
public sealed record GameTitleConflictResponse(bool Applied, long GameId, string Reason)
{
    public static GameTitleConflictResponse From(GameTitleEditResult result) =>
        new(false, result.GameId, string.IsNullOrWhiteSpace(result.Reason) ? "La edición no se pudo aplicar." : result.Reason);
}
