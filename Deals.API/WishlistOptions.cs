namespace Deals.API;

public sealed class WishlistOptions
{
    public const string SectionName = "Wishlist";

    public bool Enabled { get; set; } = true;
    public int RunAtHour { get; set; } = 3;
    public int JitterMinutes { get; set; } = 10;
    public string TimeZoneId { get; set; } = "America/Mexico_City";
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>Hourly ceiling of per-game price refreshes: the background pass spaces calls by 3600000/this ms.</summary>
    public int MaxRefreshesPerHour { get; set; } = 1000;

    /// <summary>
    /// Minimum hours between the recovery pass of one host start and the recorded run of a previous one.
    /// Guards deploys, crash loops and local debugging: without it, every process start replays the whole
    /// pass (the price walk alone is tens of minutes). The scheduled slot is never gated by this value.
    /// </summary>
    public int MinHoursBetweenRuns { get; set; } = 20;

    public string ApiBaseUrl { get; set; } = "https://api.steampowered.com/";
    public int TimeoutSeconds { get; set; } = 10;
}
