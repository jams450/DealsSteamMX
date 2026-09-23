namespace Deals.BusinessLogic.Exceptions;

/// <summary>
/// Raised when the cover source cannot be consulted at all (unconfigured, auth failure, non-2xx). The API
/// maps it to 503: no write happens, so the caller can retry later. It is deliberately not derived from
/// <see cref="GameCoverSourceNotFoundException"/> — "unavailable" and "not found" are different outcomes
/// with different status codes.
/// </summary>
public sealed class GameCoverSourceUnavailableException()
    : Exception("El proveedor de portadas no está disponible.");
