namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// The four <c>user_library</c> columns an import is allowed to overwrite, and therefore the only ones that
/// can make a reimport count as a real modification (<c>odd/tasks/library-import-summary.md</c>, S3).
///
/// <para>
/// <c>imported_at</c> is deliberately absent: every pass rewrites it, so treating it as a change would report
/// every matched row as modified — the exact false "modificado" the summary exists to avoid. The columns the
/// import never writes (<c>itad_game_id</c>, <c>priority</c>, the acquisition fields) are absent for the
/// mirror reason: a value the import does not touch can never be a difference it caused.
/// </para>
///
/// <see cref="AddedAt"/> and the title are nullable/preserved exactly as the row stores them, so the
/// comparison runs on the stored value and the merged value, never on the raw payload.
/// </summary>
public readonly record struct LibraryImportRowState(
    string Title,
    bool? IsInstalled,
    DateTime? AddedAt,
    long? GameId);

/// <summary>
/// The one rule that decides what a reimport changed, shared by both import endpoints and DB-free so
/// <c>checks/LibraryImportSummary.Checks</c> can pin it: <see cref="Merge"/> produces exactly the values the
/// loop assigns to the tracked row, and <see cref="IsRealChange"/> compares that result with what the row
/// already held. The loop classifies the merged state it is about to write, so the report cannot drift from
/// the write.
/// </summary>
public static class LibraryImportChanges
{
    /// <summary>
    /// The state the loop writes over the stored one. The date follows
    /// <see cref="LibraryImportDates.Merge"/>: a payload that carries no date (Heroic) keeps the stored one
    /// instead of erasing it, and a Playnite payload always carries its own. The canonical id is the one the
    /// resolver returned, which is how a row imported before the catalog still gains its identity.
    /// </summary>
    public static LibraryImportRowState Merge(
        LibraryImportRowState stored,
        string title,
        bool isInstalled,
        DateTime? incomingAddedAt,
        long? resolvedGameId) =>
        new(
            title,
            isInstalled,
            LibraryImportDates.Merge(stored.AddedAt, incomingAddedAt),
            resolvedGameId);

    /// <summary>
    /// True when the merged state differs from the stored one in any business column. Ordinal title
    /// comparison: a case-only change is still a change, because the title is displayed as written.
    /// </summary>
    public static bool IsRealChange(LibraryImportRowState stored, LibraryImportRowState merged) =>
        !string.Equals(stored.Title, merged.Title, StringComparison.Ordinal) ||
        stored.IsInstalled != merged.IsInstalled ||
        stored.AddedAt != merged.AddedAt ||
        stored.GameId != merged.GameId;
}

/// <summary>
/// One accepted new row of the import report, for the completion modal: the title as imported and the
/// platform, which is the canonical store key (<see cref="StoreKeys"/>) the row was accepted under — never an
/// OS inferred from anything else.
/// </summary>
public sealed record LibraryImportItem(string Title, string Platform);

/// <summary>
/// The report both import endpoints return, accumulated inside the transaction that writes the rows
/// (<c>odd/tasks/library-import-summary.md</c>, S2/S3).
///
/// <para>
/// <see cref="Updated"/> keeps its legacy meaning — every accepted row that matched an existing one — and
/// <see cref="Modified"/> is the subset where a business column actually changed, so an old consumer reading
/// <c>updated</c> sees exactly what it saw before this field existed. Nothing is ever deleted: both imports
/// upsert and never prune, so <see cref="Deleted"/> is a constant 0 and not a count of anything the loop
/// looked at.
/// </para>
///
/// <para>
/// <see cref="AddedItems"/> is bounded server-side for the modal. Up to
/// <see cref="MaxListedItems"/> added rows (inclusive) the whole list is returned; from
/// <see cref="MaxListedItems"/> + 1 the list is empty and the count is the only report. A truncated preview
/// would claim the import stopped at 50, so there is no partial list.
/// </para>
/// </summary>
public sealed class LibraryImportSummary
{
    /// <summary>
    /// How many added rows can be named. 50 rows is inclusive; 51 or more returns no list at all.
    /// </summary>
    public const int MaxListedItems = 50;

    private readonly HashSet<(string Store, string StoreGameId, string State)> _seen = [];
    private readonly List<LibraryImportItem> _added = [];

    /// <summary>Rows inserted by this pass.</summary>
    public int Imported { get; private set; }

    /// <summary>Accepted rows that matched an existing one — the legacy <c>updated</c> count, changed or not.</summary>
    public int Updated { get; private set; }

    /// <summary>Matched rows where a business column actually changed before the write.</summary>
    public int Modified { get; private set; }

    /// <summary>Always 0: neither import prunes, so there is nothing to report as deleted.</summary>
    public int Deleted => 0;

    /// <summary>The added rows, complete up to <see cref="MaxListedItems"/>, empty beyond it.</summary>
    public IReadOnlyList<LibraryImportItem> AddedItems =>
        Imported <= MaxListedItems ? _added.ToList() : [];

    /// <summary>Always empty: matches <see cref="Deleted"/>.</summary>
    public IReadOnlyList<LibraryImportItem> DeletedItems => [];

    /// <summary>
    /// Registers the row key of the loop iteration and reports whether it should be processed. The key is
    /// the exact <c>(store, store_game_id, state)</c> triple <c>user_library</c> is keyed by, so a payload
    /// that repeats an entry keeps the first occurrence and is counted once — the same rule the loop applied
    /// before this report existed, now owned by the object that counts.
    /// </summary>
    public bool TryBegin(string store, string storeGameId, string state) =>
        _seen.Add((store, storeGameId, state));

    /// <summary>Counts an insert and, while the list is still under the bound, records it for the modal.</summary>
    public void CountAdded(string title, string platform)
    {
        Imported++;
        if (_added.Count < MaxListedItems)
        {
            _added.Add(new LibraryImportItem(title, platform));
        }
    }

    /// <summary>
    /// Counts a matched row. It is always a legacy <c>updated</c>; it is a <c>modified</c> only when
    /// <see cref="stored"/> and <see cref="merged"/> differ in a business column (see
    /// <see cref="LibraryImportChanges.IsRealChange"/>). Call it before the row is written, with the values
    /// the loop is about to assign.
    /// </summary>
    public void CountUpdated(LibraryImportRowState stored, LibraryImportRowState merged)
    {
        Updated++;
        if (LibraryImportChanges.IsRealChange(stored, merged))
        {
            Modified++;
        }
    }
}
