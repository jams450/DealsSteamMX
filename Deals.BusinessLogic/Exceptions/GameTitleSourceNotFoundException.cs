namespace Deals.BusinessLogic.Exceptions;

/// <summary>
/// Raised when the title source answered and has no row for the requested id. Distinct from
/// <see cref="GameNotFoundException"/> (the canonical row does not exist) and from an unavailable provider:
/// the API maps this to 404 and nothing is written.
/// </summary>
public sealed class GameTitleSourceNotFoundException(long sourceId)
    : Exception($"No existe un juego con el id {sourceId} en el proveedor de títulos.")
{
    public long SourceId { get; } = sourceId;
}
