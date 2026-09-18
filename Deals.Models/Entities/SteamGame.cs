using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

[Table("steam_games")]
public class SteamGame : BaseModel
{
    [Key]
    [Column("steam_game_id")]
    public int SteamGameId { get; set; }

    [Column("app_id")]
    public int AppId { get; set; }

    [Column("name")]
    [Required]
    [StringLength(512)]
    public string Name { get; set; } = string.Empty;

    [Column("type")]
    [StringLength(32)]
    public string? Type { get; set; }

    [Column("image_url")]
    [StringLength(512)]
    public string? ImageUrl { get; set; }

    [Column("is_free")]
    public bool IsFree { get; set; }

    [Column("currency")]
    [StringLength(3)]
    public string? Currency { get; set; }

    [Column("initial_price_minor")]
    public int? InitialPriceMinor { get; set; }

    [Column("current_price_minor")]
    public int? CurrentPriceMinor { get; set; }

    [Column("discount_percent")]
    public int? DiscountPercent { get; set; }

    [Column("lowest_price_minor")]
    public int? LowestPriceMinor { get; set; }

    [Column("lowest_price_at", TypeName = "timestamp with time zone")]
    public DateTime? LowestPriceAt { get; set; }

    [Column("itad_game_id")]
    [StringLength(36)]
    public string? ItadGameId { get; set; }

    [Column("offers_refreshed_at", TypeName = "timestamp with time zone")]
    public DateTime? OffersRefreshedAt { get; set; }

    [Column("ggdeals_refreshed_at", TypeName = "timestamp with time zone")]
    public DateTime? GgDealsRefreshedAt { get; set; }

    /// <summary>
    /// Last time the external bundles of this game were refreshed. Independent of the offer timestamps:
    /// a bundle refresh failure must not mark the offers stale, nor the other way around.
    /// </summary>
    [Column("bundles_refreshed_at", TypeName = "timestamp with time zone")]
    public DateTime? BundlesRefreshedAt { get; set; }

    [Column("region")]
    [Required]
    [StringLength(2)]
    public string Region { get; set; } = "mx";

    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }

    public ICollection<SteamPriceObservation> PriceObservations { get; set; } = new List<SteamPriceObservation>();

    public ICollection<GameOffer> Offers { get; set; } = new List<GameOffer>();

    public ICollection<ExternalBundleGame> BundleLinks { get; set; } = new List<ExternalBundleGame>();
}
