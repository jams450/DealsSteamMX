namespace Deals.API;

/// <summary>Local safety budgets for outbound provider work. These are not provider quota claims.</summary>
public sealed class JobBudgetOptions
{
    public const string SectionName = "JobBudgets";
    public ProviderBudgetOptions Itad { get; set; } = new();
    public ProviderBudgetOptions GgDeals { get; set; } = new();
}

public sealed class ProviderBudgetOptions
{
    public int Minute { get; set; } = 6;
    public int Hour { get; set; } = 60;
    public int Day { get; set; } = 500;
    public int Burst { get; set; } = 2;
    public int MinDelayMilliseconds { get; set; } = 1000;
}
