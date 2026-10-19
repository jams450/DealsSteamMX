namespace Deals.BusinessLogic.Exceptions;

/// <summary>
/// Raised when another identity transaction held <c>dealext.game_identity</c> for longer than the wait
/// budget. The API maps it to 409 Conflict: nothing was written, and the caller can retry in a moment.
/// It is deliberately not a 500 - the request was valid, the gate was busy - and deliberately not a
/// wait: an unbounded wait is what pinned pooled connections.
/// </summary>
public sealed class IdentityLockUnavailableException()
    : Exception("Otra operación de identidad está en curso.");
