using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// One bundle the user reports as purchased (specs S3-S6). It is the acquisition record and the
/// idempotency key at once: <c>UNIQUE (user_id, source, bundle_key)</c> means a repeated submit of the
/// same bundle answers from this row instead of importing again.
///
/// <para>
/// The row carries its own frozen copy of the provider snapshot (<see cref="SnapshotJson"/> and
/// <see cref="SnapshotHash"/>) plus the purchased tier's facts, so the membership the person bought is
/// still readable after the upstream refresh rewrites <c>external_bundles.tiers_json</c>. It is
/// deliberately not foreign-keyed to <c>external_bundles</c>: the purchase is evidence and must survive a
/// provider purge.
/// </para>
///
/// <para>
/// <see cref="Platform"/> is the tier-wide library the games were imported into; per-game overrides live
/// in <see cref="BundlePurchaseGame.Platform"/>. The reminder pipeline reads this table before dispatching
/// a red bundle alert, which is why a purchase commits before anything is queued for it.
/// </para>
/// </summary>
[Table("bundle_purchases")]
public class BundlePurchase : BaseModel
{
    [Key]
    [Column("bundle_purchase_id")]
    public long BundlePurchaseId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    /// <summary>Provider source of the bundle (<c>itad</c>, <c>itad_global</c>, <c>ggdeals</c>).</summary>
    [Column("source")]
    [Required]
    [StringLength(16)]
    public string Source { get; set; } = string.Empty;

    [Column("bundle_key")]
    [Required]
    [StringLength(128)]
    public string BundleKey { get; set; } = string.Empty;

    [Column("title")]
    [Required]
    [StringLength(512)]
    public string Title { get; set; } = string.Empty;

    [Column("shop_id")]
    [StringLength(32)]
    public string? ShopId { get; set; }

    [Column("shop_name")]
    [StringLength(128)]
    public string? ShopName { get; set; }

    [Column("page_url")]
    [StringLength(1024)]
    public string? PageUrl { get; set; }

    [Column("deal_url")]
    [StringLength(1024)]
    public string? DealUrl { get; set; }

    [Column("published_at", TypeName = "timestamp with time zone")]
    public DateTime? PublishedAt { get; set; }

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Provider observation timestamp of the snapshot that was purchased.</summary>
    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }

    /// <summary>Stable key of the purchased tier (sha256 of its content).</summary>
    [Column("tier_key")]
    [Required]
    [StringLength(64)]
    public string TierKey { get; set; } = string.Empty;

    [Column("tier_index")]
    public int TierIndex { get; set; }

    [Column("tier_price_minor")]
    public int? TierPriceMinor { get; set; }

    [Column("tier_currency")]
    [StringLength(3)]
    public string? TierCurrency { get; set; }

    [Column("tier_addon")]
    public bool TierAddon { get; set; }

    [Column("tier_items_complete")]
    public bool TierItemsComplete { get; set; }

    /// <summary>The provider's "choose N of M" count; null for a fixed (whole-membership) tier.</summary>
    [Column("tier_selection_count")]
    public int? TierSelectionCount { get; set; }

    /// <summary><c>fixed</c> or <c>choice</c>, mirrored by a CHECK.</summary>
    [Column("selection_mode")]
    [Required]
    [StringLength(8)]
    public string SelectionMode { get; set; } = string.Empty;

    [Column("selected_count")]
    public int SelectedCount { get; set; }

    /// <summary>Tier-wide platform the games were imported into (<c>user_library.store</c> vocabulary).</summary>
    [Column("platform")]
    [Required]
    [StringLength(32)]
    public string Platform { get; set; } = string.Empty;

    [Column("acquired_at", TypeName = "date")]
    public DateOnly? AcquiredAt { get; set; }

    /// <summary>Optional shop the bundle was bought from; null means "not registered".</summary>
    [Column("acquired_store")]
    [StringLength(32)]
    public string? AcquiredStore { get; set; }

    [Column("acquired_store_type")]
    [StringLength(16)]
    public string? AcquiredStoreType { get; set; }

    [Column("acquisition_provider")]
    [StringLength(16)]
    public string? AcquisitionProvider { get; set; }

    /// <summary>sha256 (hex) of <see cref="SnapshotJson"/>, the value the confirm had to repeat.</summary>
    [Column("snapshot_hash")]
    [Required]
    [StringLength(64)]
    public string SnapshotHash { get; set; } = string.Empty;

    /// <summary>
    /// Frozen canonical projection of the provider tier payload at confirm time (actual items included).
    /// It is the snapshot the hash covers, so a rewrite of <c>external_bundles.tiers_json</c> can never
    /// rewrite what this purchase says was bought.
    /// </summary>
    [Column("snapshot_json", TypeName = "jsonb")]
    public string SnapshotJson { get; set; } = string.Empty;

    public User? User { get; set; }

    public ICollection<BundlePurchaseGame> Games { get; set; } = new List<BundlePurchaseGame>();
}
