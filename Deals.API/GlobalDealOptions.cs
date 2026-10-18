namespace Deals.API;

/// <summary>
/// Configuration of the global ITAD deals sweep (<c>deals/v2</c>), the paginated discovery feed of
/// good deals for games outside the wishlist. It is deliberately **disabled by default**: the sweep
/// spends the shared ITAD governor budget and the feed is a new integration, so it must be turned on
/// explicitly after the migration is applied and the provider contract is certified.
/// </summary>
public sealed class GlobalDealOptions
{
    public const string SectionName = "GlobalDeals";

    /// <summary>Provider cap for one <c>deals/v2</c> page.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Background sweep switch. Off until configured on purpose.</summary>
    public bool Enabled { get; set; }

    /// <summary>Items requested per page. Clamped to <see cref="MaxPageSize"/> by the client.</summary>
    public int PageSize { get; set; } = MaxPageSize;

    /// <summary>
    /// Maximum pages one sweep may fetch. The provider's <c>hasMore</c> at this budget is a truncation:
    /// the sweep preserves the previous snapshot instead of certifying a partial catalog.
    /// </summary>
    public int MaxPages { get; set; } = 20;

    /// <summary>Hours between scheduled sweeps.</summary>
    public int RefreshHours { get; set; } = 24;

    /// <summary>Upper bound of the random minutes added to each scheduled interval.</summary>
    public int JitterMinutes { get; set; } = 30;

    /// <summary>Delay before the first pass of a host start.</summary>
    public int StartupDelaySeconds { get; set; } = 300;

    /// <summary>Minimum hours between the recovery pass of a host start and a previous run.</summary>
    public int MinHoursBetweenRuns { get; set; } = 12;

    /// <summary>
    /// Local "good deal" threshold in percent, applied to the documented <c>cut</c> (or the
    /// regular-vs-current arithmetic). It is a policy value, not a provider claim.
    /// </summary>
    public int MinDiscountPercent { get; set; } = 50;
}
