using System.Text.Json;
using System.Text.Json.Serialization;
using Deals.BusinessLogic.Models.Library;

namespace Deals.API.Models.Games;

/// <summary>
/// Body of a manual cover pick. The shape is discriminated and strict: exactly one of
/// <c>{ "steamAppId": 123 }</c> or <c>{ "igdbId": 456 }</c>, and no other property at all. Sending both,
/// neither, an extra member or a non-positive id is a 400. The client never sends a URL or a title — the
/// server re-reads the provider row by the id it picked, so a caller cannot point the catalog at an
/// arbitrary image. A null value counts as "not sent", the same convention <c>GameTitleRequest</c> uses.
/// </summary>
public sealed class GameCoverRequest
{
    /// <summary><c>steam</c> source: required, > 0, and forbidden together with <see cref="IgdbId"/>.</summary>
    // Strict numbers: the MVC web defaults read "620" as 620, and a body that only has to be understood
    // loosely is not the discriminated shape this endpoint promises.
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? SteamAppId { get; set; }

    /// <summary><c>igdb</c> source: required, > 0, and forbidden together with <see cref="SteamAppId"/>.</summary>
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long? IgdbId { get; set; }

    /// <summary>
    /// Any member the two known fields do not cover. It exists only to be rejected: an added <c>imageUrl</c>
    /// or <c>title</c> is a malformed body (400), never a silently ignored field.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>
    /// Strictly maps the wire body to the business command. An ambiguous shape is an
    /// <see cref="ArgumentException"/>, which the global handler reports as 400 with nothing written and no
    /// provider call.
    /// </summary>
    public GameCoverCommand ToCommand()
    {
        if (AdditionalProperties is { Count: > 0 })
        {
            throw new ArgumentException(
                "El cuerpo de la portada solo admite steamAppId o igdbId.", nameof(AdditionalProperties));
        }

        var hasSteamAppId = SteamAppId is not null;
        var hasIgdbId = IgdbId is not null;

        if (hasSteamAppId == hasIgdbId)
        {
            throw new ArgumentException(
                "Envía exactamente uno: steamAppId o igdbId.", nameof(SteamAppId));
        }

        if (hasSteamAppId)
        {
            if (SteamAppId <= 0)
            {
                throw new ArgumentException("El appid de Steam debe ser mayor que cero.", nameof(SteamAppId));
            }

            return new GameCoverCommand(GameCoverSource.Steam, SteamAppId, null);
        }

        if (IgdbId <= 0)
        {
            throw new ArgumentException("El id de IGDB debe ser mayor que cero.", nameof(IgdbId));
        }

        return new GameCoverCommand(GameCoverSource.Igdb, null, IgdbId);
    }
}

/// <summary>
/// Cover after a manual pick. <see cref="ImageUrl"/> is what the API stored — the provider URL the server
/// re-read — so the page paints the server value instead of the one it asked for.
/// </summary>
public sealed record GameCoverResponse(string ImageUrl);
