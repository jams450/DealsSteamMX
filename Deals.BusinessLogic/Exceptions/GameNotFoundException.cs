namespace Deals.BusinessLogic.Exceptions;

/// <summary>
/// Raised when a game id named by a mutation does not exist. The API maps it to 404, which is deliberately
/// distinct from the refused-merge outcome (409): a missing row is a bad request, not a business rule.
/// </summary>
public sealed class GameNotFoundException(long gameId)
    : Exception($"No existe el juego canónico {gameId}.")
{
    public long GameId { get; } = gameId;
}
