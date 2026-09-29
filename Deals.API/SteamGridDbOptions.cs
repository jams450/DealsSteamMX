namespace Deals.API;

/// <summary>
/// Transport bounds of the SteamGridDB client (the last source of the automatic cover chain). Only the
/// transport lives here: the API key is read by the client from the flat setting
/// <c>STEAMGRIDDB_API_KEY</c>, next to IGDB's <c>IGDB_CLIENT_ID</c>/<c>IGDB_CLIENT_SECRET</c>, because it is a
/// per-provider secret and not a storefront setting — and because a section-bound key would be a second,
/// silently different way of naming the same credential.
/// </summary>
public sealed class SteamGridDbOptions
{
    public const string SectionName = "SteamGridDb";

    /// <summary>API host with the <c>/api/v2</c> path already included; the client appends the endpoints.</summary>
    public string BaseUrl { get; set; } = "https://www.steamgriddb.com/api/v2/";

    public int TimeoutSeconds { get; set; } = 10;
}
