using Deals.BusinessLogic.Models.Itad;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>Why a global bundle sweep did not produce an authoritative snapshot.</summary>
public enum GlobalBundleSweepIncompleteReason
{
    /// <summary>The sweep completed: the provider returned fewer items than the page limit.</summary>
    None = 0,

    /// <summary>The page budget ran out while the last page was still full: the catalog may have more.</summary>
    PageBudgetExhausted,

    /// <summary>A page repeated an earlier page, so the provider is ignoring the offset and the sweep can
    /// never advance. Treating the loop as "finished" would certify a partial catalog.</summary>
    RepeatedPage,

    /// <summary>A page request or its parse failed. The previous snapshot is preserved.</summary>
    PageFailure
}

/// <summary>
/// Pure accumulator for the paginated global bundle sweep. It owns completion only: it performs no I/O,
/// so the "is this snapshot complete?" rule is deterministic and testable without a provider or a
/// database. A page with fewer items than the limit is the end of the catalog; a full page at the page
/// budget is a truncation; a page whose key set repeats an earlier page means the provider ignored the
/// offset. Anything other than <see cref="GlobalBundleSweepIncompleteReason.None"/> means the caller must
/// preserve the previous snapshot and its timestamp.
/// </summary>
public sealed class GlobalBundleSweep
{
    private readonly int limit;
    private readonly int maxPages;
    private readonly List<ItadBundle> bundles = [];
    private readonly HashSet<string> bundleKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pageFingerprints = new(StringComparer.Ordinal);

    public GlobalBundleSweep(int limit, int maxPages)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "ITAD caps bundles/v1 limit at 50.");
        }

        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        }

        this.limit = limit;
        this.maxPages = maxPages;
    }

    public int PageCount { get; private set; }

    public GlobalBundleSweepIncompleteReason IncompleteReason { get; private set; } =
        GlobalBundleSweepIncompleteReason.None;

    /// <summary>True once the catalog ended inside the budget without a failure or a repeated page.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Every distinct bundle seen so far, in first-seen order.</summary>
    public IReadOnlyList<ItadBundle> Bundles => bundles;

    /// <summary>True while another page must be fetched.</summary>
    public bool NeedsAnotherPage => !IsComplete && IncompleteReason == GlobalBundleSweepIncompleteReason.None;

    /// <summary>
    /// Accepts one fetched page in provider order. Returns true when another page must be fetched. A full
    /// page means the provider may have more; a page under the limit ends the catalog. A page whose bundle
    /// key set repeats any earlier page stops the sweep as incomplete.
    /// </summary>
    public bool AddPage(IReadOnlyList<ItadBundle> page)
    {
        PageCount++;

        if (!pageFingerprints.Add(Fingerprint(page)))
        {
            IncompleteReason = GlobalBundleSweepIncompleteReason.RepeatedPage;
            return false;
        }

        foreach (var bundle in page)
        {
            if (bundleKeys.Add(bundle.BundleKey))
            {
                bundles.Add(bundle);
            }
        }

        if (page.Count < limit)
        {
            IsComplete = true;
            return false;
        }

        if (PageCount >= maxPages)
        {
            IncompleteReason = GlobalBundleSweepIncompleteReason.PageBudgetExhausted;
            return false;
        }

        return true;
    }

    /// <summary>Marks the sweep failed after a page request or parse threw.</summary>
    public void MarkFailed()
    {
        IncompleteReason = GlobalBundleSweepIncompleteReason.PageFailure;
    }

    /// <summary>
    /// Order-independent fingerprint of one page: the provider owns the order inside a page, so only the
    /// key set matters when asking "did offset advance?".
    /// </summary>
    private static string Fingerprint(IReadOnlyList<ItadBundle> page) =>
        string.Join(
            '\n',
            page.Select(bundle => bundle.BundleKey).OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
}
