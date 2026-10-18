namespace Deals.API;

/// <summary>
/// Configuration of the gg.deals active bundle sweep (<c>bundles/active/</c>). The provider documents a
/// global active catalog and a one-hour cursor lifetime; the sweep is deliberately conservative because
/// it spends the shared GG.deals governor budget (100 records/minute, 1000/hour) and its
/// <c>active/</c> requests are not batched. Off by default until an operator enables it with a key.
/// </summary>
public sealed class GgDealsBundleOptions
{
    public const string SectionName = "GgDealsBundles";

    /// <summary>New integration: disabled until explicitly configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum pages one sweep may fetch before the catalog is declared truncated.</summary>
    public int MaxPages { get; set; } = 40;

    /// <summary>Mirrors the provider's documented one-hour cursor lifetime.</summary>
    public int CursorValidityMinutes { get; set; } = 60;

    /// <summary>Hours between scheduled sweeps.</summary>
    public int RefreshHours { get; set; } = 24;

    /// <summary>Upper bound of the random minutes added to each scheduled interval.</summary>
    public int JitterMinutes { get; set; } = 15;

    /// <summary>Delay before the first pass of a host start.</summary>
    public int StartupDelaySeconds { get; set; } = 300;

    /// <summary>Minimum hours between the recovery pass of a host start and a previous run.</summary>
    public int MinHoursBetweenRuns { get; set; } = 12;
}
