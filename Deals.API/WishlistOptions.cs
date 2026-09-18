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

    public string ApiBaseUrl { get; set; } = "https://api.steampowered.com/";
    public int TimeoutSeconds { get; set; } = 10;
}
