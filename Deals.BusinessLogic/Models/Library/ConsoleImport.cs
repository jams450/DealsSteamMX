namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// One row of a Playnite export whose <c>Source</c> is null (a console/manual game, not a store purchase),
/// as the console bulk import reads it (<c>docs/PLAN_CONSOLE.md</c> §7). <c>Platforms</c> is
/// <em>metadata</em>: it only feeds the platform suggestions of the preview and never decides ownership or
/// identity. <c>EntryId</c> is the Playnite game id and the key that ties a preview entry to its commit
/// decision.
/// </summary>
public sealed record ConsoleImportEntryInput(
    string? EntryId,
    string? Name,
    string? Source,
    IReadOnlyList<string>? Platforms,
    bool? IsInstalled,
    string? Added);

/// <summary>
/// One platform the catalog can infer from a Playnite display name. A suggestion to pre-select in the
/// dialog: the person may pick another platform or type a new slug, and only that explicit choice is
/// written (<see cref="ConsoleImportDecisionInput.OwnedPlatform"/>).
/// </summary>
public sealed record ConsoleImportPlatformSuggestion(string Slug, string DisplayName, string Source);

/// <summary>
/// One preview entry: an identifiable row (index + Playnite id) with the catalog candidates for its title
/// and the suggested platforms. Nothing here asserts identity or ownership — the preview only reads.
/// </summary>
public sealed record ConsoleImportPreviewEntry(
    int Index,
    string EntryId,
    string Name,
    IReadOnlyList<ManualGameCandidate> Candidates,
    IReadOnlyList<ConsoleImportPlatformSuggestion> SuggestedPlatforms,
    bool NeedsPlatform);

/// <summary>Read-only result of the console import preview, one element per input entry, same order.</summary>
public sealed record ConsoleImportPreviewResult(IReadOnlyList<ConsoleImportPreviewEntry> Entries);

/// <summary>
/// One explicit commit decision per preview entry. Exactly one identity action is required:
/// <see cref="AttachGameId"/> (attach to that existing canonical game) or <see cref="Create"/> (insert a new
/// canonical game). <see cref="OwnedPlatform"/> is the platform the person chose; it is validated with
/// <see cref="StoreKeys.Normalize"/> and the eight PC stores are refused.
/// </summary>
public sealed record ConsoleImportDecisionInput(
    string? EntryId,
    string? Name,
    string? OwnedPlatform,
    long? AttachGameId,
    bool Create,
    bool? IsInstalled,
    string? Added);

/// <summary>Per-entry outcome of a commit, keyed by the Playnite entry id.</summary>
public sealed record ConsoleImportEntryResult(
    string EntryId,
    string Outcome,
    string Platform,
    long GameId,
    long? UserLibraryId);

/// <summary>
/// An identity conflict that refuses the whole commit before any write: the
/// <c>(platform, canonical game id)</c> mapping already belongs to another canonical game, so linking the
/// row would contradict the merge truth. <see cref="OwnerGameId"/> is the game that owns the mapping today.
/// </summary>
public sealed record ConsoleImportConflict(string EntryId, string Platform, long GameId, long OwnerGameId);

/// <summary>
/// Result of a console import commit. <see cref="Applied"/> false is a refused commit (409): nothing was
/// written, and <see cref="Conflicts"/> says why. Counts are per entry outcome; <see cref="Entries"/> is
/// empty on a refusal.
/// </summary>
public sealed record ConsoleImportCommitResult(
    bool Applied,
    int Created,
    int Attached,
    int AlreadyPresent,
    IReadOnlyList<ConsoleImportEntryResult> Entries,
    IReadOnlyList<ConsoleImportConflict> Conflicts)
{
    public static ConsoleImportCommitResult Refused(IReadOnlyList<ConsoleImportConflict> conflicts) =>
        new(false, 0, 0, 0, [], conflicts);
}

/// <summary>Wire-visible outcome codes of a console import commit.</summary>
public static class ConsoleImportOutcomes
{
    /// <summary>A new canonical game was inserted and the library row attached to it.</summary>
    public const string Created = "created";

    /// <summary>The library row hangs off an existing canonical game the person picked, or off one that already had a row.</summary>
    public const string Attached = "attached";

    /// <summary>The exact <c>(user, platform, store_game_id, owned)</c> row already exists: no write, not an error.</summary>
    public const string AlreadyPresent = "already_present";
}

/// <summary>Bounds of the console bulk import, shared by the API contract and the service.</summary>
public static class ConsoleImportLimits
{
    /// <summary>Hard cap per payload: the preview issues a fixed number of batch queries regardless of size.</summary>
    public const int MaxEntries = 2000;

    /// <summary>
    /// <c>user_library.title</c> is <c>VARCHAR(256)</c> even though <c>games.title</c> allows 512: the row is
    /// the binding constraint, so a longer name is a 400 instead of a Postgres 22001 surfacing as a 500.
    /// Mirrors <c>ManualLibraryService</c>.
    /// </summary>
    public const int MaxTitleLength = 256;

    /// <summary>Candidate cap per entry, mirroring the manual-add dialog.</summary>
    public const int MaxCandidatesPerEntry = 10;
}
