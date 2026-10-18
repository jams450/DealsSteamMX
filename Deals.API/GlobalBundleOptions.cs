namespace Deals.API;

/// <summary>
/// Configuration of the global ITAD bundle sweep (<c>bundles/v1</c>), the paginated catalog of every
/// active MX bundle. Values are deliberately conservative: the sweep spends the shared ITAD governor
/// budget, so it runs at most a few times a day and jitters its schedule instead of stampeding the
/// provider at a fixed hour.
/// </summary>
public sealed class GlobalBundleOptions
{
    public const string SectionName = "GlobalBundles";

    /// <summary>Provider cap for one <c>bundles/v1</c> page.</summary>
    public const int MaxPageSize = 50;

    public bool Enabled { get; set; } = true;

    /// <summary>Items requested per page. Clamped to <see cref="MaxPageSize"/> by the client.</summary>
    public int PageSize { get; set; } = MaxPageSize;

    /// <summary>
    /// Maximum pages one sweep may fetch. A full page at this budget is a truncation: the sweep preserves
    /// the previous snapshot instead of certifying a partial catalog.
    /// </summary>
    public int MaxPages { get; set; } = 40;

    /// <summary>Hours between scheduled sweeps.</summary>
    public int RefreshHours { get; set; } = 12;

    /// <summary>Upper bound of the random minutes added to each scheduled interval.</summary>
    public int JitterMinutes { get; set; } = 10;

    /// <summary>Delay before the first pass of a host start.</summary>
    public int StartupDelaySeconds { get; set; } = 120;

    /// <summary>Minimum hours between the recovery pass of a host start and a previous run.</summary>
    public int MinHoursBetweenRuns { get; set; } = 6;
}
