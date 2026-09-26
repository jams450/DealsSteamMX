namespace Deals.API;

/// <summary>Local safety budgets for outbound provider work. Values stay below verified provider quotas.</summary>
public sealed class JobBudgetOptions
{
    public const string SectionName = "JobBudgets";

    public ItadBudgetOptions Itad { get; set; } = new();
    public GgDealsBudgetOptions GgDeals { get; set; } = new();
    public int RetentionDays { get; set; } = 1;
    public int PurgeBatchSize { get; set; } = 500;
}

/// <summary>ITAD quota is 1000 requests per 300 seconds; this local budget leaves 10% margin.</summary>
public sealed class ItadBudgetOptions
{
    public int RequestsPerFiveMinutes { get; set; } = 900;
    public int MaxBurst { get; set; } = 25;
    public int MinDelayMilliseconds { get; set; } = 200;
}

/// <summary>GG.deals quota is 100 records/minute and 1000 records/hour; this leaves 10% margin.</summary>
public sealed class GgDealsBudgetOptions
{
    public int RecordsPerMinute { get; set; } = 90;
    public int RecordsPerHour { get; set; } = 900;
    public int MaxBurstRecords { get; set; } = 90;
    public int MinDelayMilliseconds { get; set; } = 200;
}
