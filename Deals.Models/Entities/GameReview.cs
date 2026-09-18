using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// One per-platform review of a canonical game, owned by its author. Deliberately keyed by
/// <c>(user_id, game_id, platform)</c> and NOT foreign-keyed to <see cref="UserLibrary"/>: a library row is
/// an import artifact whose unique key includes <c>state</c>, so a reimport or a state change recreates it.
/// <c>(game_id, platform)</c> is stable and survives both. The score label is computed, never persisted.
/// </summary>
[Table("game_reviews")]
public class GameReview : BaseModel
{
    [Key]
    [Column("game_review_id")]
    public long GameReviewId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("game_id")]
    public long GameId { get; set; }

    /// <summary>Same vocabulary as <c>user_library.store</c>, so a review joins a library row by text.</summary>
    [Column("platform")]
    [Required]
    [StringLength(32)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>Day 1 of the month, or null. The wire contract uses <c>YYYY-MM</c>.</summary>
    [Column("started_month", TypeName = "date")]
    public DateTime? StartedMonth { get; set; }

    /// <summary>Day 1 of the month, or null. Never earlier than <see cref="StartedMonth"/>.</summary>
    [Column("finished_month", TypeName = "date")]
    public DateTime? FinishedMonth { get; set; }

    /// <summary>0..100, or null when unscored. Validated in the service, not by a CHECK constraint.</summary>
    [Column("score")]
    public short? Score { get; set; }

    [Column("is_goty")]
    public bool IsGoty { get; set; }

    [Column("body")]
    public string? Body { get; set; }
}
