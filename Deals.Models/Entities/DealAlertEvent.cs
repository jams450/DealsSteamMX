using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// Durable outbox row for one ntfy deal alert. The producer inserts it once per dedup identity; the
/// dispatcher claims due rows and delivers them. The message text is stored with the row so delivery
/// never depends on the live snapshot still existing at send time.
///
/// <see cref="DedupKey"/> (policy version + game + source + offer + price) carries the unique index, so
/// re-running the producer on an unchanged deal conflicts and inserts nothing. A stale row (the offer
/// moved or vanished) is retired with <see cref="DealAlertStatuses.Stale"/> instead of being sent.
/// </summary>
[Table("deal_alert_events")]
public class DealAlertEvent : BaseModel
{
    [Key]
    [Column("deal_alert_event_id")]
    public long DealAlertEventId { get; set; }

    [Column("dedup_key")]
    [Required]
    [StringLength(320)]
    public string DedupKey { get; set; } = string.Empty;

    [Column("steam_game_id")]
    public int? SteamGameId { get; set; }

    [Column("game_id")]
    public long? GameId { get; set; }

    [Column("source")]
    [Required]
    [StringLength(16)]
    public string Source { get; set; } = string.Empty;

    [Column("offer_key")]
    [Required]
    [StringLength(128)]
    public string OfferKey { get; set; } = string.Empty;

    [Column("price_minor")]
    public int PriceMinor { get; set; }

    [Column("trigger_version")]
    public int TriggerVersion { get; set; }

    /// <summary>Comma-joined <see cref="Deals.BusinessLogic.Models.Notifications.DealAlertReasons"/>.</summary>
    [Column("reasons")]
    [Required]
    [StringLength(64)]
    public string Reasons { get; set; } = string.Empty;

    [Column("score", TypeName = "numeric(4,1)")]
    public decimal? Score { get; set; }

    [Column("title")]
    [Required]
    [StringLength(512)]
    public string Title { get; set; } = string.Empty;

    [Column("message")]
    [Required]
    [StringLength(1024)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional provider offer URL used as the ntfy Click target. Never a token-bearing URL.</summary>
    [Column("click_url")]
    [StringLength(1024)]
    public string? ClickUrl { get; set; }

    [Column("status")]
    [Required]
    [StringLength(16)]
    public string Status { get; set; } = DealAlertStatuses.Pending;

    [Column("attempts")]
    public int Attempts { get; set; }

    [Column("next_attempt_at", TypeName = "timestamp with time zone")]
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Sanitized failure kind (outcome name or HTTP status). Never an exception body.</summary>
    [Column("last_error")]
    [StringLength(256)]
    public string? LastError { get; set; }

    [Column("sent_at", TypeName = "timestamp with time zone")]
    public DateTime? SentAt { get; set; }
}

/// <summary>Lifecycle of an outbox row. Plain strings, matching the repo's status conventions.</summary>
public static class DealAlertStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";

    /// <summary>The offer no longer qualifies (moved, expired, disappeared). Retired, not sent.</summary>
    public const string Stale = "stale";
}
