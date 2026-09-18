using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// Canonical external bundle snapshot, keyed by (source, bundle_key) and shared by every game it was
/// seen in. Not an offer: bundles never take part in the price comparison and are never written to
/// <see cref="GameOffer"/>.
/// </summary>
[Table("external_bundles")]
public class ExternalBundle : BaseModel
{
    [Key]
    [Column("external_bundle_id")]
    public long ExternalBundleId { get; set; }

    [Column("source")]
    [Required]
    [StringLength(16)]
    public string Source { get; set; } = string.Empty;

    /// <summary>Stable identifier of the bundle inside its source.</summary>
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

    /// <summary>Bundle page URL reported by the provider, stored verbatim after HTTPS validation.</summary>
    [Column("page_url")]
    [StringLength(1024)]
    public string? PageUrl { get; set; }

    /// <summary>
    /// Provider bundle URL including its affiliate tag, stored verbatim after HTTPS validation. Never
    /// rewritten, trimmed or rebuilt: attribution depends on the link surviving as sent.
    /// </summary>
    [Column("deal_url")]
    [StringLength(1024)]
    public string? DealUrl { get; set; }

    /// <summary>Short provider description, already stripped of control characters and truncated.</summary>
    [Column("details")]
    [StringLength(600)]
    public string? Details { get; set; }

    [Column("published_at", TypeName = "timestamp with time zone")]
    public DateTime? PublishedAt { get; set; }

    /// <summary>
    /// Sanitized tier list serialized as JSON (jsonb). Only the display shape is stored - price, currency,
    /// addon and item titles/types - so no provider payload and no item ids are persisted. Serialized and
    /// deserialized with the same options by the service.
    /// </summary>
    [Column("tiers_json", TypeName = "jsonb")]
    public string? TiersJson { get; set; }

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTime? ExpiresAt { get; set; }

    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }

    public ICollection<ExternalBundleGame> Links { get; set; } = new List<ExternalBundleGame>();
}
