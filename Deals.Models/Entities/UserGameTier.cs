using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// A user's placement of one reviewed play in an annual tier list. The identity is the review — not the
/// game — because a game can be played twice in the same year on the same platform, and each play ranks
/// on its own (spec S5). <see cref="GameId"/> is kept as the merge anchor: a canonical merge repoints it
/// together with the review, and <c>SQL/checks/game_tiers_reviews.sql</c> asserts both rows agree.
/// </summary>
[Table("user_game_tiers")]
public sealed class UserGameTier : BaseModel
{
    [Column("user_id")]
    public int UserId { get; set; }

    /// <summary>
    /// The year the card is shown under. Denormalized on purpose: it is derived from the review's
    /// <c>finished_month ?? started_month</c>, but a review can be edited later, so this column is the
    /// scope the user last saved into and the service refuses a year that does not match the review.
    /// </summary>
    [Column("year")]
    public int Year { get; set; }

    /// <summary>The reviewed play this placement belongs to. Cascades on review delete.</summary>
    [Column("game_review_id")]
    public long GameReviewId { get; set; }

    /// <summary>Canonical game of the review, kept as the merge anchor. Cascades on game delete.</summary>
    [Column("game_id")]
    public long GameId { get; set; }

    [Column("tier")]
    [Required]
    [StringLength(1)]
    public string Tier { get; set; } = "S";

    [Column("position")]
    public int Position { get; set; }
}
