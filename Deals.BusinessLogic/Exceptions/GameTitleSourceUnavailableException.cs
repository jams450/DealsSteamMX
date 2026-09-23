namespace Deals.BusinessLogic.Exceptions;

/// <summary>
/// Raised when the title source cannot be consulted at all (unconfigured, auth failure, non-2xx). The API
/// maps it to 503: no write happens, so the caller can retry later. It is deliberately not derived from
/// <see cref="GameTitleSourceNotFoundException"/> — "unavailable" and "not found" are different outcomes
/// with different status codes.
/// </summary>
public sealed class GameTitleSourceUnavailableException()
    : Exception("El proveedor de títulos no está disponible.");
