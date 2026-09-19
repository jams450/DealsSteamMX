using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Deals.Models.Entities;

/// <summary>
/// Audit log of one manual canonical merge. Deliberately does NOT inherit <c>BaseModel</c>: the table
/// has no <c>created_at</c>/<c>updated_at</c>/<c>created_by</c>/<c>updated_by</c> columns — a merge is an
/// event, not an editable row, so <see cref="MergedAt"/>/<see cref="MergedBy"/> are its whole audit.
/// No FK to <see cref="Game"/> on purpose: the absorbed game no longer exists and the log must outlive it.
/// </summary>
[Table("game_merges")]
public class GameMerge
{
    [Key]
    [Column("game_merge_id")]
    public long GameMergeId { get; set; }

    [Column("survivor_game_id")]
    public long SurvivorGameId { get; set; }

    /// <summary>The deleted game. Not a foreign key: that row is gone by the time this log is read.</summary>
    [Column("absorbed_game_id")]
    public long AbsorbedGameId { get; set; }

    /// <summary>JSONB held as text. Only the irrecoverable rows: the absorbed game, its external ids and
    /// the reviews that were deleted. Repointed rows (steam_games, user_library) are not copied.</summary>
    [Column("absorbed_snapshot")]
    public string AbsorbedSnapshot { get; set; } = string.Empty;

    [Column("moved_external_ids")]
    public int MovedExternalIds { get; set; }

    [Column("moved_steam_games")]
    public int MovedSteamGames { get; set; }

    [Column("moved_library_rows")]
    public int MovedLibraryRows { get; set; }

    [Column("dropped_reviews")]
    public int DroppedReviews { get; set; }

    [Column("merged_at")]
    public DateTime MergedAt { get; set; }

    [Column("merged_by")]
    [StringLength(100)]
    public string? MergedBy { get; set; }
}
