using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Bulk import of a Playnite export's console/manual rows (<c>docs/PLAN_CONSOLE.md</c> §7): a read-only
/// preview that offers candidates and platform suggestions, then a commit that writes only the decisions a
/// person sent. It is a parallel contract to <c>POST /api/library/import</c>, which stays frozen and never
/// accepts <c>Source: null</c>.
///
/// <para>
/// Identity rules are the manual-add rules: the id is always the canonical <c>games.game_id</c>, written as
/// <c>user_library.store_game_id</c> and claimed in <c>game_external_ids</c> for the platform namespace. A
/// title only ever produces candidates; the commit either attaches to a game the person named or explicitly
/// creates one.
/// </para>
/// </summary>
public interface IConsoleLibraryImportService
{
    /// <summary>
    /// Reads the catalog for every entry: candidates by normalized title (suggestions, never an identity
    /// claim) and the platform slugs the catalog can infer from the Playnite platform names. Never writes.
    /// Returns null when the user no longer exists (the caller maps it to 404); throws
    /// <see cref="ArgumentException"/> on an invalid payload, which the API maps to 400.
    /// </summary>
    Task<ConsoleImportPreviewResult?> PreviewAsync(
        int userId,
        IReadOnlyList<ConsoleImportEntryInput> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the explicit decisions in one transaction under the global identity lock. The whole payload is
    /// validated before any write; an identity conflict refuses the commit with no write at all. Rows are
    /// insert-only (an exact row already present is reported, never updated) and PC stores are refused.
    /// Returns null when the user no longer exists (404); throws <see cref="ArgumentException"/> on an
    /// invalid payload (400).
    /// </summary>
    Task<ConsoleImportCommitResult?> CommitAsync(
        int userId,
        IReadOnlyList<ConsoleImportDecisionInput> decisions,
        CancellationToken cancellationToken = default);
}
