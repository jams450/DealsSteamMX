using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>A user's placement of a canonical game in an annual review tier list.</summary>
[Table("user_game_tiers")]
public sealed class UserGameTier : BaseModel
{
    [Column("user_id")]
    public int UserId { get; set; }

    [Column("year")]
    public int Year { get; set; }

    [Column("game_id")]
    public long GameId { get; set; }

    [Column("tier")]
    [Required]
    [StringLength(1)]
    public string Tier { get; set; } = "S";

    [Column("position")]
    public int Position { get; set; }
}
