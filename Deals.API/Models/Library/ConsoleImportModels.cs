namespace Deals.API.Models.Library;

using Deals.BusinessLogic.Models.Library;

/// <summary>
/// One entry of a Playnite export sent to the console preview (<c>docs/PLAN_CONSOLE.md</c> §7). Same shape
/// as <see cref="PlayniteLibraryEntry"/> plus <see cref="Platforms"/>; extra export fields are ignored by
/// design. <see cref="Source"/> must be null here — the console export is by definition the rows Playnite
/// has no store plugin for. Store rows keep going to <c>POST /api/library/import</c>, which never accepts a
/// null Source: neither path can swallow the other's payload.
/// </summary>
public sealed class ConsoleImportEntryRequest
{
    /// <summary>Playnite game id; it is the entry key and travels back as <c>entryId</c>.</summary>
    public string? GameId { get; set; }

    public string? Source { get; set; }

    public string? Name { get; set; }

    /// <summary>Playnite platform names. Metadata only: they feed the suggestions, never ownership.</summary>
    public List<string>? Platforms { get; set; }

    public bool? IsInstalled { get; set; }

    public string? Added { get; set; }
}

/// <summary>
/// One explicit commit decision for a preview entry. <see cref="OwnedPlatform"/> is what the person chose
/// (validated with <c>StoreKeys.Normalize</c>; the eight PC stores are refused) and exactly one identity
/// action is required: <see cref="AttachGameId"/> or <see cref="Create"/>.
/// </summary>
public sealed class ConsoleImportDecisionRequest
{
    /// <summary>Entry key echoed by the preview (<c>entryId</c>).</summary>
    public string? EntryId { get; set; }

    public string? Name { get; set; }

    public string? OwnedPlatform { get; set; }

    /// <summary>Existing canonical game to attach the row to.</summary>
    public long? AttachGameId { get; set; }

    /// <summary>Insert a new canonical game for this entry.</summary>
    public bool Create { get; set; }

    public bool? IsInstalled { get; set; }

    public string? Added { get; set; }
}

/// <summary>One platform the catalog can infer from a Playnite display name; a suggestion, not a decision.</summary>
public sealed record ConsoleImportPlatformResponse(string Slug, string DisplayName, string Source)
{
    public static ConsoleImportPlatformResponse From(ConsoleImportPlatformSuggestion suggestion) =>
        new(suggestion.Slug, suggestion.DisplayName, suggestion.Source);
}

/// <summary>
/// One preview entry: an identifiable row with the catalog candidates for its title (suggestions only:
/// identity is never asserted by title) and the suggested platforms. <c>NeedsPlatform</c> means nothing
/// could be suggested and the dialog must ask.
/// </summary>
public sealed record ConsoleImportPreviewEntryResponse(
    int Index,
    string EntryId,
    string Name,
    IReadOnlyList<GameCandidateResponse> Candidates,
    IReadOnlyList<ConsoleImportPlatformResponse> SuggestedPlatforms,
    bool NeedsPlatform)
{
    public static ConsoleImportPreviewEntryResponse From(ConsoleImportPreviewEntry entry) => new(
        entry.Index,
        entry.EntryId,
        entry.Name,
        entry.Candidates.Select(GameCandidateResponse.From).ToList(),
        entry.SuggestedPlatforms.Select(ConsoleImportPlatformResponse.From).ToList(),
        entry.NeedsPlatform);
}

/// <summary>Read-only preview result, one element per input entry, in the same order.</summary>
public sealed record ConsoleImportPreviewResponse(IReadOnlyList<ConsoleImportPreviewEntryResponse> Entries)
{
    public static ConsoleImportPreviewResponse From(ConsoleImportPreviewResult result) =>
        new(result.Entries.Select(ConsoleImportPreviewEntryResponse.From).ToList());
}

/// <summary>Per-entry commit outcome, keyed by <c>entryId</c>.</summary>
public sealed record ConsoleImportCommitEntryResponse(
    string EntryId,
    string Outcome,
    string Platform,
    long GameId,
    long? UserLibraryId)
{
    public static ConsoleImportCommitEntryResponse From(ConsoleImportEntryResult result) =>
        new(result.EntryId, result.Outcome, result.Platform, result.GameId, result.UserLibraryId);
}

/// <summary>
/// An identity conflict that refused the whole commit before any write: the <c>(platform, game id)</c>
/// mapping already belongs to <see cref="OwnerGameId"/>.
/// </summary>
public sealed record ConsoleImportConflictResponse(string EntryId, string Platform, long GameId, long OwnerGameId)
{
    public static ConsoleImportConflictResponse From(ConsoleImportConflict conflict) =>
        new(conflict.EntryId, conflict.Platform, conflict.GameId, conflict.OwnerGameId);
}

/// <summary>
/// Result of a console import commit. <c>Applied</c> false is a 409 with nothing written; the counts are per
/// outcome (<c>created | attached | already_present</c>) and <c>entries</c> is empty on a refusal.
/// </summary>
public sealed record ConsoleImportCommitResponse(
    bool Applied,
    int Created,
    int Attached,
    int AlreadyPresent,
    IReadOnlyList<ConsoleImportCommitEntryResponse> Entries,
    IReadOnlyList<ConsoleImportConflictResponse> Conflicts)
{
    public static ConsoleImportCommitResponse From(ConsoleImportCommitResult result) => new(
        result.Applied,
        result.Created,
        result.Attached,
        result.AlreadyPresent,
        result.Entries.Select(ConsoleImportCommitEntryResponse.From).ToList(),
        result.Conflicts.Select(ConsoleImportConflictResponse.From).ToList());
}
