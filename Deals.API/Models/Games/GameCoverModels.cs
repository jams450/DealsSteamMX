namespace Deals.API.Models.Games;

/// <summary>
/// Body of a manual cover pick. The client sends the appid, never the URL: Steam is the only source of the
/// stored artwork, so a caller cannot point the catalog at an arbitrary image.
/// </summary>
public sealed record GameCoverRequest(int SteamAppId);

/// <summary>
/// Cover after a manual pick. <see cref="ImageUrl"/> is what the API stored, so the page paints the server
/// value instead of the one it asked for.
/// </summary>
public sealed record GameCoverResponse(string ImageUrl);
