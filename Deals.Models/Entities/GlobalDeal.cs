using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// One individual deal from the global ITAD deals catalog (<c>deals/v2</c>), keyed by
/// (source, deal_key). It is its own table, not a bundle: bundles never take part in the deal feed and
/// this feed never writes an offer into <c>game_offers</c>, so it cannot disturb the per-game price
/// comparison.
///
/// The original currency/amount columns are the source of truth. The MXN columns are derived and
/// nullable: only <c>pricing_type = 'regional'</c> (MXN quote) and <c>pricing_type = 'fx_estimate'</c>
/// (USD quote converted with the persisted rate) carry a usable MXN amount. Any other currency is
/// <c>unconverted</c> with no MXN claim, and is not eligible for the price policy.
/// </summary>
[Table("global_deals")]
public class GlobalDeal : BaseModel
{
    [Key]
    [Column("global_deal_id")]
    public long GlobalDealId { get; set; }

    /// <summary>Dedicated source literal (<c>itad_global_deals</c>), never <c>itad</c>.</summary>
    [Column("source")]
    [Required]
    [StringLength(32)]
    public string Source { get; set; } = string.Empty;

    /// <summary>Stable identity of the deal inside its source (the provider deal id).</summary>
    [Column("deal_key")]
    [Required]
    [StringLength(128)]
    public string DealKey { get; set; } = string.Empty;

    /// <summary>ITAD game UUID the deal belongs to; the exact key for wishlist exclusion.</summary>
    [Column("itad_game_id")]
    [Required]
    [StringLength(36)]
    public string ItadGameId { get; set; } = string.Empty;

    [Column("game_title")]
    [Required]
    [StringLength(512)]
    public string GameTitle { get; set; } = string.Empty;

    [Column("game_slug")]
    [StringLength(256)]
    public string? GameSlug { get; set; }

    [Column("game_type")]
    [StringLength(32)]
    public string? GameType { get; set; }

    [Column("game_mature")]
    public bool GameMature { get; set; }

    [Column("shop_id")]
    [Required]
    [StringLength(32)]
    public string ShopId { get; set; } = string.Empty;

    [Column("shop_name")]
    [Required]
    [StringLength(128)]
    public string ShopName { get; set; } = string.Empty;

    /// <summary>True when the shop is in the configured official allowlist. Never inferred from payload.</summary>
    [Column("is_official")]
    public bool IsOfficial { get; set; }

    [Column("original_currency")]
    [Required]
    [StringLength(3)]
    public string OriginalCurrency { get; set; } = string.Empty;

    [Column("original_current_price_minor")]
    public int OriginalCurrentPriceMinor { get; set; }

    [Column("original_regular_price_minor")]
    public int? OriginalRegularPriceMinor { get; set; }

    /// <summary>Documented <c>cut</c>, or regular-vs-current arithmetic when <c>cut</c> is absent.</summary>
    [Column("discount_percent")]
    public int? DiscountPercent { get; set; }

    /// <summary>Provider-reported history low, only when the payload actually carries it. Never inferred.</summary>
    [Column("history_low_all_minor")]
    public int? HistoryLowAllMinor { get; set; }

    [Column("history_low_currency")]
    [StringLength(3)]
    public string? HistoryLowCurrency { get; set; }

    [Column("mxn_current_price_minor")]
    public int? MxnCurrentPriceMinor { get; set; }

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

    /// <summary>Provider deal URL, stored verbatim after HTTPS validation (affiliate tag survives).</summary>
    [Column("deal_url")]
    [StringLength(1024)]
    public string? DealUrl { get; set; }

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTime? ExpiresAt { get; set; }

    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }
}
