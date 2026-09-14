using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

[Table("steam_price_observations")]
public class SteamPriceObservation : BaseModel
{
    [Key]
    [Column("steam_price_observation_id")]
    public long SteamPriceObservationId { get; set; }

    [Column("steam_game_id")]
    public int SteamGameId { get; set; }

    [Column("currency")]
    [StringLength(3)]
    public string? Currency { get; set; }

    [Column("initial_price_minor")]
    public int? InitialPriceMinor { get; set; }

    [Column("current_price_minor")]
    public int? CurrentPriceMinor { get; set; }

    [Column("discount_percent")]
    public int? DiscountPercent { get; set; }

    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }

    [ForeignKey(nameof(SteamGameId))]
    public SteamGame SteamGame { get; set; } = null!;
}
