namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>
/// Bounds of one global deals sweep. The provider caps <c>deals/v2</c> at 200 items per page, and the
/// page budget bounds how far a single run may page before it is declared truncated.
/// </summary>
public sealed record GlobalDealSyncSettings(int PageSize, int MaxPages);

/// <summary>
/// Outcome of one global deals sweep. <see cref="Completed"/> is true only when the provider reported
/// the end of the catalog inside the budget; when it is false nothing was written and the previous
/// snapshot and its observed timestamp stand.
/// </summary>
public sealed record GlobalDealSyncResult(
    bool Completed,
    GlobalDealSweepIncompleteReason IncompleteReason,
    int Pages,
    int Deals,
    int Written,
    int Removed)
{
    public static GlobalDealSyncResult Preserved(GlobalDealSweep sweep) =>
        new(false, sweep.IncompleteReason, sweep.PageCount, sweep.Deals.Count, 0, 0);

    public static GlobalDealSyncResult Applied(GlobalDealSweep sweep, GlobalDealWriteResult write) =>
        new(true, GlobalDealSweepIncompleteReason.None, sweep.PageCount, sweep.Deals.Count,
            write.Written, write.Removed);
}

/// <summary>Rows written and removed by one atomic snapshot replacement.</summary>
public sealed record GlobalDealWriteResult(int Written, int Removed);
