namespace Deals.API;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public int MaxFailedAttempts { get; init; } = 5;
    public int LockMinutes { get; init; } = 15;
    public int RefreshDays { get; init; } = 30;
}
