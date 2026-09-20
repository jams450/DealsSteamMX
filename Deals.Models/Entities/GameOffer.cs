using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// Current offer snapshot for a game, keyed by (game, region, source, offer_key). Not history.
/// The original price columns are the source of truth; the MXN columns are derived and nullable.
/// </summary>
[Table("game_offers")]
public class GameOffer : BaseModel
{
    [Key]
    [Column("game_offer_id")]
    public long GameOfferId { get; set; }

    /// <summary>
    /// Steam snapshot the offer used to hang off exclusively. Nullable since Fase 2: a store can sell a game
    /// Steam does not (<c>source='microsoft'</c>), and that offer has no <see cref="SteamGameId"/> to point
    /// at. Such a row requires <see cref="GameId"/>, which is what makes the canonical key enforce its
    /// uniqueness.
    /// </summary>
    [Column("steam_game_id")]
    public int? SteamGameId { get; set; }

    /// <summary>
    /// Canonical anchor (docs/PLAN_MULTISTORE.md §5), derived from <see cref="SteamGameId"/>. Nullable
    /// because <see cref="SteamGame.GameId"/> is nullable by design: NULL here means the game has no
    /// canonical identity yet, exactly like <c>user_library.game_id</c>.
    /// </summary>
    [Column("game_id")]
    public long? GameId { get; set; }

    /// <summary>
    /// Country the offer was priced for. Never null: the price depends on it and it is part of the
    /// unique key, so a NULL region would let two rows exist for the same (game, source, offer key).
    /// </summary>
    [Column("region")]
    [Required]
    [StringLength(2)]
    public string Region { get; set; } = string.Empty;

    [Column("source")]
    [Required]
    [StringLength(16)]
    public string Source { get; set; } = string.Empty;

    [Column("offer_key")]
    [Required]
    [StringLength(128)]
    public string OfferKey { get; set; } = string.Empty;

    [Column("shop_id")]
    [StringLength(32)]
    public string? ShopId { get; set; }

    [Column("shop_name")]
    [Required]
    [StringLength(128)]
    public string ShopName { get; set; } = string.Empty;

    [Column("classification")]
    [Required]
    [StringLength(16)]
    public string Classification { get; set; } = string.Empty;

    [Column("original_currency")]
    [Required]
    [StringLength(3)]
    public string OriginalCurrency { get; set; } = string.Empty;

    [Column("original_regular_price_minor")]
    public int? OriginalRegularPriceMinor { get; set; }

    [Column("original_current_price_minor")]
    public int? OriginalCurrentPriceMinor { get; set; }

    [Column("mxn_regular_price_minor")]
    public int? MxnRegularPriceMinor { get; set; }

    [Column("mxn_current_price_minor")]
    public int? MxnCurrentPriceMinor { get; set; }

    /// <summary>
    /// Provider-neutral lowest price ever seen for the offer, in minor units. Nullable: only
    /// providers that report it fill it (ITAD historyLow.all, gg.deals historicalRetail /
    /// historicalKeyshops).
    /// </summary>
    [Column("history_low_all_minor")]
    public int? HistoryLowAllMinor { get; set; }

    /// <summary>
    /// Currency of <see cref="HistoryLowAllMinor"/>, provider-neutral. Nullable: null when the
    /// provider reported no history low.
    /// </summary>
    [Column("history_low_currency")]
    [StringLength(3)]
    public string? HistoryLowCurrency { get; set; }

    [Column("fx_rate", TypeName = "numeric(18,8)")]
    public decimal? FxRate { get; set; }

    [Column("fx_rate_date", TypeName = "date")]
    public DateOnly? FxRateDate { get; set; }

    [Column("fx_source")]
    [StringLength(32)]
    public string? FxSource { get; set; }

    [Column("pricing_type")]
    [Required]
    [StringLength(16)]
    public string PricingType { get; set; } = string.Empty;

    [Column("discount_percent")]
    public int? DiscountPercent { get; set; }

    [Column("deal_url")]
    [StringLength(1024)]
    public string? DealUrl { get; set; }

    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }

    /// <summary>
    /// Provider-neutral DRM names reported with the offer. Snapshot, not identity: overwritten on
    /// every successful refresh. Empty means the provider reported none.
    /// </summary>
    [Column("drm_names", TypeName = "text[]")]
    public string[] DrmNames { get; set; } = [];

    /// <summary>
    /// Provider-neutral platform names reported with the offer. Snapshot, not identity: overwritten
    /// on every successful refresh. Empty means the provider reported none.
    /// </summary>
    [Column("platform_names", TypeName = "text[]")]
    public string[] PlatformNames { get; set; } = [];

    public SteamGame? SteamGame { get; set; }
}
