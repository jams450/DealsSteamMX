namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// Bounds of one global bundle sweep. The provider caps <c>bundles/v1</c> at 50 items per page, and the
/// page budget bounds how far a single run may page before it is declared truncated.
/// </summary>
public sealed record GlobalBundleSyncSettings(int PageSize, int MaxPages);

/// <summary>
/// Outcome of one global bundle sweep. <see cref="Completed"/> is true only when the provider returned a
/// full catalog inside the budget; when it is false nothing was written and the previous snapshot and its
/// observed timestamp stand.
/// </summary>
public sealed record GlobalBundleSyncResult(
    bool Completed,
    GlobalBundleSweepIncompleteReason IncompleteReason,
    int Pages,
    int Bundles,
    int Written,
    int Removed)
{
    public static GlobalBundleSyncResult Preserved(GlobalBundleSweep sweep) =>
        new(false, sweep.IncompleteReason, sweep.PageCount, sweep.Bundles.Count, 0, 0);

    public static GlobalBundleSyncResult Applied(GlobalBundleSweep sweep, GlobalBundleWriteResult write) =>
        new(true, GlobalBundleSweepIncompleteReason.None, sweep.PageCount, sweep.Bundles.Count,
            write.Written, write.Removed);
}

/// <summary>Rows written and removed by one atomic snapshot replacement.</summary>
public sealed record GlobalBundleWriteResult(int Written, int Removed);
