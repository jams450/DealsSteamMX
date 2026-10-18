namespace Deals.BusinessLogic.Models.GgDeals;

/// <summary>Why a gg.deals active-catalog sweep did not produce an authoritative snapshot.</summary>
public enum GgDealsBundleSweepIncompleteReason
{
    /// <summary>The sweep reached <c>next: null</c> and its collected total matched the provider total.</summary>
    None = 0,

    /// <summary>The page budget ran out while <c>next</c> was still present: the catalog may have more.</summary>
    PageBudgetExhausted,

    /// <summary>The provider returned a <c>next</c> link already followed, so the enumeration can loop.</summary>
    RepeatedCursor,

    /// <summary>Two pages reported the same bundle-key fingerprint, so the provider is not advancing.</summary>
    RepeatedPage,

    /// <summary>The running total changed between pages or did not match the final provider total.</summary>
    TotalMismatch,

    /// <summary>
    /// The one-hour cursor validity elapsed before the catalog ended. The sweep stops and preserves; it
    /// never restarts from the first page inside the same run, because a restart would spend a fresh
    /// budget on an enumeration the provider already signalled is unstable.
    /// </summary>
    CursorExpired,

    /// <summary>A page request or its parse failed. The previous snapshot is preserved.</summary>
    PageFailure
}

/// <summary>
/// Bounds of one gg.deals active-catalog sweep. <see cref="MaxPages"/> bounds the enumeration and
/// <see cref="CursorValidityMinutes"/> mirrors the provider's documented one-hour cursor lifetime.
/// </summary>
public sealed record GgDealsBundleSyncSettings(int MaxPages, int CursorValidityMinutes);

/// <summary>
/// Outcome of one gg.deals active-catalog sweep. <see cref="Completed"/> is true only when the provider
/// reached <c>next: null</c> with a matching total inside the budget and the cursor window; when it is
/// false nothing was written and the previous snapshot and its observed timestamp stand.
/// </summary>
public sealed record GgDealsBundleSyncResult(
    bool Completed,
    GgDealsBundleSweepIncompleteReason IncompleteReason,
    int Pages,
    int Bundles,
    int Written,
    int Removed)
{
    public static GgDealsBundleSyncResult Preserved(GgDealsBundleSweep sweep) =>
        new(false, sweep.IncompleteReason, sweep.PageCount, sweep.Bundles.Count, 0, 0);

    public static GgDealsBundleSyncResult Applied(GgDealsBundleSweep sweep, int written, int removed) =>
        new(true, GgDealsBundleSweepIncompleteReason.None, sweep.PageCount, sweep.Bundles.Count, written, removed);
}

/// <summary>
/// Pure accumulator for the paginated gg.deals active sweep. It performs no I/O, so the "is this
/// snapshot complete?" rule is deterministic and testable without a provider or a database. Every
/// reason other than <see cref="GgDealsBundleSweepIncompleteReason.None"/> means the caller must
/// preserve the previous snapshot and its timestamp.
///
/// Cursor rule: a page is accepted with the wall-clock time it was read. The first accepted page sets
/// the cursor window's origin. If another page is still needed after the provider's documented
/// one-hour lifetime, the sweep stops as <see cref="GgDealsBundleSweepIncompleteReason.CursorExpired"/>
/// and never restarts from page one in the same run.
/// </summary>
public sealed class GgDealsBundleSweep
{
    private readonly int maxPages;
    private readonly TimeSpan cursorValidity;
    private readonly List<GgDealsBundle> bundles = [];
    private readonly HashSet<string> bundleKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> pageFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenCursors = new(StringComparer.Ordinal);
    private DateTime firstPageAt;

    public GgDealsBundleSweep(int maxPages, TimeSpan cursorValidity)
    {
        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        }

        if (cursorValidity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorValidity));
        }

        this.maxPages = maxPages;
        this.cursorValidity = cursorValidity;
    }

    public int PageCount { get; private set; }

    /// <summary>Provider total reported by the first page; -1 until a page is accepted.</summary>
    public int TotalCount { get; private set; } = -1;

    public GgDealsBundleSweepIncompleteReason IncompleteReason { get; private set; } =
        GgDealsBundleSweepIncompleteReason.None;

    /// <summary>True once the catalog ended inside the budget and window without a failure.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Every distinct bundle seen so far, in first-seen order.</summary>
    public IReadOnlyList<GgDealsBundle> Bundles => bundles;

    /// <summary>True while another page must be fetched.</summary>
    public bool NeedsAnotherPage => !IsComplete && IncompleteReason == GgDealsBundleSweepIncompleteReason.None;

    /// <summary>
    /// Accepts one fetched page read at <paramref name="nowUtc"/>. Returns true when another page must
    /// be fetched. A <c>next</c> of null ends the catalog, but only when the distinct total matches the
    /// provider total; any other mismatch stops as incomplete.
    /// </summary>
    public bool AddPage(GgDealsBundlePage page, DateTime nowUtc)
    {
        PageCount++;
        if (PageCount == 1)
        {
            firstPageAt = nowUtc;
        }

        if (!pageFingerprints.Add(Fingerprint(page)))
        {
            IncompleteReason = GgDealsBundleSweepIncompleteReason.RepeatedPage;
            return false;
        }

        foreach (var bundle in page.Bundles)
        {
            if (bundleKeys.Add(bundle.BundleKey))
            {
                bundles.Add(bundle);
            }
        }

        if (TotalCount < 0)
        {
            TotalCount = page.TotalCount;
        }
        else if (page.TotalCount != TotalCount)
        {
            IncompleteReason = GgDealsBundleSweepIncompleteReason.TotalMismatch;
            return false;
        }

        if (page.NextCursor is null)
        {
            if (bundles.Count != TotalCount)
            {
                IncompleteReason = GgDealsBundleSweepIncompleteReason.TotalMismatch;
                return false;
            }

            IsComplete = true;
            return false;
        }

        if (!seenCursors.Add(page.NextCursor))
        {
            IncompleteReason = GgDealsBundleSweepIncompleteReason.RepeatedCursor;
            return false;
        }

        if (PageCount >= maxPages)
        {
            IncompleteReason = GgDealsBundleSweepIncompleteReason.PageBudgetExhausted;
            return false;
        }

        if (nowUtc - firstPageAt > cursorValidity)
        {
            IncompleteReason = GgDealsBundleSweepIncompleteReason.CursorExpired;
            return false;
        }

        return true;
    }

    /// <summary>Marks the sweep failed after a page request or parse threw.</summary>
    public void MarkFailed()
    {
        IncompleteReason = GgDealsBundleSweepIncompleteReason.PageFailure;
    }

    /// <summary>
    /// Order-independent fingerprint of one page: the provider owns the order inside a page, so only
    /// the key set matters when asking "did the enumeration advance?".
    /// </summary>
    private static string Fingerprint(GgDealsBundlePage page) =>
        string.Join('\n', page.Bundles.Select(bundle => bundle.BundleKey).OrderBy(key => key, StringComparer.Ordinal));
}
