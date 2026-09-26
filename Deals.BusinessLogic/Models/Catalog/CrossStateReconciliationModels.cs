namespace Deals.BusinessLogic.Models.Catalog;

public sealed record CrossStateStoreRow(
    string Store,
    string StoreGameId,
    string State,
    string Title,
    int? Priority,
    bool? IsInstalled);

public sealed record CrossStateExternalId(string Namespace, string ExternalId);

public sealed record CrossStateMember(
    long GameId,
    string Title,
    string NormalizedTitle,
    string? Type,
    int? ReleaseYear,
    IReadOnlyList<string> States,
    IReadOnlyList<CrossStateStoreRow> StoreRows,
    IReadOnlyList<CrossStateExternalId> ExternalIds,
    IReadOnlyList<string> SteamAppIds,
    IReadOnlyList<string> Evidence,
    bool Blocked,
    string? BlockReason);

public sealed record CrossStateCandidateGroup(
    string CandidateKey,
    string Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    bool Blocked,
    string? BlockReason,
    IReadOnlyList<CrossStateMember> Members);
