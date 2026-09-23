namespace Deals.BusinessLogic.Exceptions;

/// <summary>
/// Raised when the cover source answered and has no usable artwork for the requested id — either no such
/// row, or a row without a cover URL. Distinct from <see cref="GameNotFoundException"/> (the canonical row
/// does not exist) and from an unavailable provider: the API maps this to 404 and nothing is written.
/// </summary>
public sealed class GameCoverSourceNotFoundException(long sourceId)
    : Exception($"El proveedor de portadas no tiene portada para el id {sourceId}.")
{
    public long SourceId { get; } = sourceId;
}
