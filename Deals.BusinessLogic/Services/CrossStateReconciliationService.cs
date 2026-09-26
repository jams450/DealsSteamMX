using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;

namespace Deals.BusinessLogic.Services;

public sealed class CrossStateReconciliationService : ICrossStateReconciliationService
{
    private readonly IRepository _repository;
    private const string AllowedStates = "('wished','owned','subscription')";

    public CrossStateReconciliationService(IRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<CrossStateCandidateGroup>> FindCandidatesAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0) return [];

        var rows = await _repository.SqlQueryAsync<CandidateRow>(
            """
            SELECT DISTINCT g.game_id AS "GameId", g.title AS "Title", g.normalized_title AS "NormalizedTitle",
                   g.type AS "Type", g.release_year AS "ReleaseYear",
                   ul.store AS "Store", ul.store_game_id AS "StoreGameId", ul.state AS "State", ul.title AS "LibraryTitle",
                   ul.priority AS "Priority", ul.is_installed AS "IsInstalled",
                   e.namespace AS "Namespace", e.external_id AS "ExternalId"
            FROM public.user_library ul
            JOIN public.games g ON g.game_id = ul.game_id
            LEFT JOIN public.game_external_ids e ON e.game_id = g.game_id
            WHERE ul.user_id = {0} AND ul.game_id IS NOT NULL AND ul.state IN ('wished','owned','subscription')
            ORDER BY g.normalized_title, g.game_id, ul.state, ul.store, ul.store_game_id, e.namespace, e.external_id
            """, userId);

        var members = rows.GroupBy(row => row.GameId).Select(group =>
        {
            var first = group.First();
            var storeRows = group.Where(x => x.Store is not null && x.StoreGameId is not null)
                .Select(x => new CrossStateStoreRow(x.Store!, x.StoreGameId!, x.State, x.LibraryTitle, x.Priority, x.IsInstalled))
                .Distinct().OrderBy(x => x.Store).ThenBy(x => x.StoreGameId).ThenBy(x => x.State).ToList();
            var ids = group.Where(x => x.Namespace is not null && x.ExternalId is not null)
                .Select(x => new CrossStateExternalId(x.Namespace!, x.ExternalId!))
                .Distinct().OrderBy(x => x.Namespace).ThenBy(x => x.ExternalId).ToList();
            var steam = ids.Where(x => x.Namespace.Equals("steam", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList();
            return new CrossStateMember(
                first.GameId, first.Title, first.NormalizedTitle, first.Type, first.ReleaseYear,
                storeRows.Select(x => x.State).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList(),
                storeRows, ids, steam, steam.Count > 1 ? ["steamAppIdConflict"] : [],
                steam.Count > 1, steam.Count > 1 ? "El juego canónico tiene más de un appid de Steam." : null);
        }).ToList();

        var result = new List<CrossStateCandidateGroup>();
        foreach (var group in members.GroupBy(x => x.NormalizedTitle, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var memberList = group.OrderBy(x => x.GameId).ToList();
            if (memberList.Count < 2) continue;
            var stateCount = memberList.SelectMany(x => x.States).Distinct(StringComparer.Ordinal).Count();
            // This endpoint is specifically cross-state: same-title duplicates confined to one state belong
            // to the separate duplicate-suggestions flow and must not leak into this contract.
            if (stateCount <= 1) continue;

            var steamIds = memberList.SelectMany(x => x.SteamAppIds).Distinct(StringComparer.Ordinal).ToList();
            var reasons = new List<string> { "sameNormalizedTitle" };
            var warnings = new List<string>();
            if (stateCount > 1) reasons.Add("crossState");
            if (steamIds.Count > 1) warnings.Add("steamAppIdConflict");

            var canonicalTypes = memberList
                .Select(x => CanonicalType(x.Type))
                .Where(x => x is not null)
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (memberList.Any(x => CanonicalType(x.Type) is null)) warnings.Add("typeMissing");
            // Missing/unknown type is uncertainty, not evidence that two canonical games are incompatible.
            // Only distinct, known canonical types can make this candidate unsafe to merge.
            if (canonicalTypes.Count > 1) warnings.Add("typeConflict");

            // Different appids across candidate games are an explicit operator warning, not a block.
            // A member with multiple appids remains blocked: its own canonical identity is ambiguous.
            var blocked = memberList.Any(x => x.Blocked) || warnings.Contains("typeConflict");
            var blockReason = memberList.FirstOrDefault(x => x.Blocked)?.BlockReason
                ?? (warnings.Contains("typeConflict") ? "Los tipos canónicos son incompatibles." : null);
            var confidence = stateCount > 1 && memberList.Any(x => x.ExternalIds.Any(id => id.Namespace.Equals("itad", StringComparison.OrdinalIgnoreCase))) ? "high"
                : stateCount > 1 ? "medium" : "low";
            result.Add(new CrossStateCandidateGroup(
                $"normalized:{group.Key}", confidence, reasons, warnings, blocked, blockReason,
                memberList.Select(member => member with { Evidence = reasons }).ToList()));
        }
        return result;
    }

    private static string? CanonicalType(string? value)
    {
        var type = value?.Trim().ToLowerInvariant();
        return type switch
        {
            "game" or "dlc" or "demo" or "soundtrack" or "bundle" => type,
            _ => null
        };
    }

    private sealed class CandidateRow
    {
        public long GameId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string NormalizedTitle { get; set; } = string.Empty;
        public string? Type { get; set; }
        public int? ReleaseYear { get; set; }
        public string? Store { get; set; }
        public string? StoreGameId { get; set; }
        public string State { get; set; } = string.Empty;
        public string LibraryTitle { get; set; } = string.Empty;
        public string? Namespace { get; set; }
        public string? ExternalId { get; set; }
        public int? Priority { get; set; }
        public bool? IsInstalled { get; set; }
    }
}
