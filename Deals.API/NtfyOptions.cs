namespace Deals.API;

/// <summary>
/// Configuration for the global ntfy deal alerts (spec S2/S3).
///
/// Disabled by default: <see cref="Enabled"/> is false, so the hosted job returns immediately and nothing
/// is inserted or sent. The Bearer token is supplied from configuration (environment), never committed;
/// the defaults here are only the non-secret shape of the target.
/// </summary>
public sealed class NtfyOptions
{
    public const string SectionName = "Ntfy";

    /// <summary>Master switch. When false the job exits on startup without touching the outbox.</summary>
    public bool Enabled { get; set; }

    /// <summary>ntfy server root. Must be an absolute HTTPS URL ending in a slash.</summary>
    public string BaseUrl { get; set; } = "https://ntfy.amitzi.xyz/";

    /// <summary>ntfy topic the alerts are published to. One topic, one segment: no slashes.</summary>
    public string Topic { get; set; } = "Deals";

    /// <summary>Bearer token for the protected topic. Placeholder <c>SET_*</c> values are rejected when enabled.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Bounded polling interval of the job. The job never refreshes external providers.</summary>
    public int PollSeconds { get; set; } = 900;

    /// <summary>Delay before the first cycle, so a deploy does not race the whole host bootstrap.</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>HttpClient timeout for one ntfy POST. A timeout is ambiguous (at-least-once), never a hard failure.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Snapshots older than this are not comparable and are never alerted on.</summary>
    public int MaxSnapshotAgeHours { get; set; } = 72;

    /// <summary>Viable discount threshold fed to the wishlist score formula. 0-95.</summary>
    public int MinViableDiscountPercent { get; set; } = 50;

    /// <summary>Maximum outbox rows claimed and sent per cycle.</summary>
    public int DispatchBatchSize { get; set; } = 20;

    /// <summary>Attempts after which a retryable alert is retired as failed.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry delay; doubles per attempt up to <see cref="RetryMaxSeconds"/>.</summary>
    public int RetryBaseSeconds { get; set; } = 60;

    /// <summary>Ceiling of the retry backoff.</summary>
    public int RetryMaxSeconds { get; set; } = 3_600;

    /// <summary>Minimum spacing between two sends inside one cycle, so a burst cannot hammer the server.</summary>
    public int MinSecondsBetweenSends { get; set; } = 1;
}
