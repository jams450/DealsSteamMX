using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// Durable outbox row for one red bundle reminder (spec S2). It is deliberately a <b>separate</b> table from
/// <see cref="DealAlertEvent"/>: a bundle is not a per-game offer, it has no price and no interest gate, and
/// sharing the offer outbox would have meant sharing its dedup key shape, its revalidation and its recipient
/// filter — that is, changing the gates of the offer alerts to make room for a different family of alert.
///
/// <para>
/// <see cref="DedupKey"/> composes the operator, the <b>source family</b>, the <b>bundle key</b> and the
/// <b>expiry instant</b>, and it carries the unique index. That is what makes "one reminder per bundle and
/// expiry cycle" true even when the job runs every fifteen minutes, and why a bundle whose provider moved its
/// expiry is a new cycle instead of a duplicate row. <c>itad</c> and <c>itad_global</c> share a family (same
/// provider, same bundle); <c>ggdeals</c> is its own key space. No title ever takes part: a title is not
/// identity.
/// </para>
///
/// <para>
/// <see cref="ExpiresAt"/> is the <b>snapshot</b> the reminder was queued for. The dispatcher re-reads the
/// persisted bundle before every send and requires the same instant: a reminder whose expiry moved (or whose
/// bundle vanished, expired, or was acquired) is retired instead of being delivered stale.
/// </para>
/// </summary>
[Table("bundle_alert_events")]
public class BundleAlertEvent : BaseModel
{
    [Key]
    [Column("bundle_alert_event_id")]
    public long BundleAlertEventId { get; set; }

    /// <summary>Operator + source family + bundle key + expiry instant. Unique, VARCHAR(320).</summary>
    [Column("dedup_key")]
    [Required]
    [StringLength(320)]
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The configured notification operator this reminder belongs to (never inferred).</summary>
    [Column("user_id")]
    public int UserId { get; set; }

    /// <summary>Provider source of the snapshot that fired (<c>itad</c>, <c>itad_global</c>, <c>ggdeals</c>).</summary>
    [Column("source")]
    [Required]
    [StringLength(16)]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Dedup family of <see cref="Source"/>: <c>itad</c> for both ITAD sources, otherwise the source itself.
    /// Stored so an operator (and the read-only SQL check) can see which family the reminder belongs to.
    /// </summary>
    [Column("source_family")]
    [Required]
    [StringLength(16)]
    public string SourceFamily { get; set; } = string.Empty;

    [Column("bundle_key")]
    [Required]
    [StringLength(128)]
    public string BundleKey { get; set; } = string.Empty;

    /// <summary>Expiry instant this reminder was queued for. Revalidated before every send.</summary>
    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>Policy generation folded into <see cref="DedupKey"/> so a bump re-alerts every red bundle once.</summary>
    [Column("trigger_version")]
    public int TriggerVersion { get; set; }

    /// <summary>ntfy message title, built once and stored so delivery never depends on the live bundle.</summary>
    [Column("title")]
    [Required]
    [StringLength(512)]
    public string Title { get; set; } = string.Empty;

    /// <summary>ntfy message body, sanitized before it was stored (no control characters).</summary>
    [Column("message")]
    [Required]
    [StringLength(1024)]
    public string Message { get; set; } = string.Empty;

    /// <summary>HTTPS bundle/deal URL used as the ntfy Click target; null when the provider gave none.</summary>
    [Column("click_url")]
    [StringLength(1024)]
    public string? ClickUrl { get; set; }

    [Column("status")]
    [Required]
    [StringLength(16)]
    public string Status { get; set; } = BundleAlertStatuses.Pending;

    [Column("attempts")]
    public int Attempts { get; set; }

    [Column("next_attempt_at", TypeName = "timestamp with time zone")]
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Sanitized failure kind (outcome name). Never a URL, a topic, a token or an exception body.</summary>
    [Column("last_error")]
    [StringLength(256)]
    public string? LastError { get; set; }

    [Column("sent_at", TypeName = "timestamp with time zone")]
    public DateTime? SentAt { get; set; }
}

/// <summary>
/// Lifecycle of a bundle reminder. Same pending/sent/failed/stale vocabulary as the offer outbox plus
/// <see cref="Suppressed"/>, which is the "it was bought, so it must not be announced" outcome.
/// </summary>
public static class BundleAlertStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";

    /// <summary>The bundle moved (expiry changed, expired, vanished) before delivery. Retired, never sent.</summary>
    public const string Stale = "stale";

    /// <summary>The operator acquired the bundle before delivery. Discarded on purpose, never a failure.</summary>
    public const string Suppressed = "suppressed";
}
