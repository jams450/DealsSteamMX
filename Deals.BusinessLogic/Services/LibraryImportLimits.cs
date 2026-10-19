namespace Deals.BusinessLogic.Services;

/// <summary>
/// The widths the library import is allowed to write. They are taken from the schema rather than chosen
/// here: <c>user_library.title</c> is <c>VARCHAR(256)</c>, and both <c>user_library.store_game_id</c> and
/// <c>game_external_ids.external_id</c> are <c>VARCHAR(64)</c> (SQL/schema.sql).
///
/// They exist because a longer value does not fail on its own: it reaches PostgreSQL, fails with 22001,
/// and aborts the transaction the whole import shares, so one bad entry used to lose every good one -
/// ten thousand rows at a time. Manual and console imports already capped these fields
/// (<c>ManualLibraryService</c>, <c>ConsoleImport</c>); the Playnite import did not.
///
/// Asserted against SQL/schema.sql by <c>checks/IdentityLock.Checks</c>, so widening a column without
/// widening the guard fails the suite instead of failing an import.
/// </summary>
public static class LibraryImportLimits
{
    /// <summary>Matches <c>user_library.title VARCHAR(256)</c>.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>Matches <c>user_library.store_game_id</c> and <c>game_external_ids.external_id</c>.</summary>
    public const int MaxStoreGameIdLength = 64;
}
