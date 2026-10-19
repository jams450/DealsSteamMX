using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Interfaces;
using Npgsql;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// The single writer gate for canonical game identity. Every path that creates or reassigns a
/// <c>game_id</c> - library import, console import, manual add, title edit and merge - takes this lock, so
/// the resolver can insert <c>game_external_ids</c> and drop orphan <c>games</c> rows without a concurrent
/// merge leaving a dangling id.
///
/// The wait is bounded on purpose. The gate used to be taken with a bare lock statement under an infinite
/// command timeout, so a slow import held the lock - and every other identity writer - for the whole
/// import; when that import got stuck, its pooled connection never came back and took the pool with it.
/// <see cref="AcquireAsync"/> sets <c>lock_timeout</c> for the caller's transaction through
/// <c>SET LOCAL</c> (so the caller must already be inside one), reports an exhausted wait as "busy" instead
/// of holding on, and releases the budget as soon as the gate is taken so nothing else in the transaction
/// inherits it.
///
/// The lock key is hashed exactly as it was before this helper existed. Changing it would make two
/// binary versions block on two different keys, and imports would stop serializing against merges during
/// a rollout.
///
/// Tied to <c>checks/IdentityLock.Checks</c>, which pins the statement order, the exact key and the
/// classification below.
/// </summary>
public static class GameIdentityLock
{
    /// <summary>PostgreSQL <c>lock_not_available</c>: the wait exceeded <c>lock_timeout</c>.</summary>
    public const string LockTimeoutSqlState = "55P03";

    /// <summary>
    /// Longest a writer may wait for another identity transaction. Above any normal identity write, and
    /// below the point where answering "busy" is worse than the wait.
    /// </summary>
    public const int WaitBudgetMilliseconds = 15_000;

    private static readonly string AcquireSql =
        "SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))";

    /// <summary>
    /// Bounds the wait for the gate itself.
    /// </summary>
    private static readonly string SetLockTimeoutSql =
        $"SET LOCAL lock_timeout = {WaitBudgetMilliseconds}";

    /// <summary>
    /// Gives the rest of the transaction its previous wait behaviour back. <c>SET LOCAL</c> applies to every
    /// statement that follows it in the transaction, so leaving the budget in place would also cut short an
    /// unrelated row lock - a concurrent import and merge touching the same user's rows, for instance - and
    /// that timeout would surface as a 500 instead of the 409 this budget exists to produce.
    ///
    /// The cost is explicit: a server-level <c>lock_timeout</c> an operator configured is not in effect for
    /// the remainder of this transaction.
    /// </summary>
    private static readonly string ReleaseLockTimeoutSql = "SET LOCAL lock_timeout = 0";

    /// <summary>
    /// Takes the identity gate inside the caller's transaction, or fails with
    /// <see cref="IdentityLockUnavailableException"/> once <see cref="WaitBudgetMilliseconds"/> is spent.
    /// </summary>
    public static async Task AcquireAsync(IRepository repository)
    {
        try
        {
            await repository.ExecuteSqlRawAsync(SetLockTimeoutSql);
            await repository.ExecuteSqlRawAsync(AcquireSql);
            await repository.ExecuteSqlRawAsync(ReleaseLockTimeoutSql);
        }
        catch (PostgresException exception) when (IsLockTimeout(exception.SqlState))
        {
            throw new IdentityLockUnavailableException();
        }
    }

    /// <summary>
    /// Classifies a server error as "another identity writer holds the gate". Public so the rule can be
    /// pinned without a PostgreSQL server (checks/IdentityLock.Checks).
    /// </summary>
    public static bool IsLockTimeout(string? sqlState) =>
        string.Equals(sqlState, LockTimeoutSqlState, StringComparison.Ordinal);
}
