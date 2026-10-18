namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>Why a global deals sweep did not produce an authoritative snapshot.</summary>
public enum GlobalDealSweepIncompleteReason
{
    /// <summary>The provider reported no more pages inside the budget.</summary>
    None = 0,

    /// <summary>The page budget ran out while the provider still reported more pages.</summary>
    PageBudgetExhausted,

    /// <summary>A page repeated an earlier page: the provider ignored the offset.</summary>
    RepeatedPage,

    /// <summary>The provider cursor did not advance, so continuing would loop forever.</summary>
    CursorLoop,

    /// <summary>A page request or its parse failed. The previous snapshot is preserved.</summary>
    PageFailure
}

/// <summary>
/// Pure accumulator for the paginated global deals sweep. It performs no I/O, so the completion rule
/// is deterministic and testable without a provider or a database. The provider's explicit
/// <c>hasMore</c> decides whether the catalog ended; a repeated page, a non-advancing cursor or an
/// exhausted page budget is a truncation, never a complete catalog. Anything other than
/// <see cref="GlobalDealSweepIncompleteReason.None"/> means the caller must preserve the previous
/// snapshot and its observed timestamp.
/// </summary>
public sealed class GlobalDealSweep
{
    private readonly int limit;
    private readonly int maxPages;
    private readonly List<ItadGlobalDeal> deals = [];
    private readonly HashSet<string> dealKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pageFingerprints = new(StringComparer.Ordinal);

    public GlobalDealSweep(int limit, int maxPages)
    {
        if (limit is < 1 or > ItadGlobalDealLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"ITAD caps deals/v2 limit at {ItadGlobalDealLimits.MaxPageSize}.");
        }

        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        }

        this.limit = limit;
        this.maxPages = maxPages;
    }

    public int PageCount { get; private set; }

    public GlobalDealSweepIncompleteReason IncompleteReason { get; private set; } =
        GlobalDealSweepIncompleteReason.None;

    /// <summary>True once the provider reported the last page inside the budget.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Every distinct deal seen so far, in first-seen order.</summary>
    public IReadOnlyList<ItadGlobalDeal> Deals => deals;

    /// <summary>True while another page must be fetched.</summary>
    public bool NeedsAnotherPage => !IsComplete && IncompleteReason == GlobalDealSweepIncompleteReason.None;

    /// <summary>
    /// Accepts one fetched page and returns true when another page must be fetched.
    /// <paramref name="currentOffset"/> is the offset used for this page, used to detect a cursor that
    /// does not advance.
    /// </summary>
    public bool AddPage(GlobalDealPage page, int currentOffset)
    {
        ArgumentNullException.ThrowIfNull(page);

        PageCount++;

        // The provider's explicit flag is the only thing allowed to end the catalog. A repeated page or
        // a stalled cursor while it still says "more" is a truncation, not a boundary.
        if (!page.HasMore)
        {
            Merge(page);
            IsComplete = true;
            return false;
        }

        if (!pageFingerprints.Add(Fingerprint(page)))
        {
            IncompleteReason = GlobalDealSweepIncompleteReason.RepeatedPage;
            return false;
        }

        Merge(page);

        if (page.NextOffset is not { } next || next <= currentOffset)
        {
            IncompleteReason = GlobalDealSweepIncompleteReason.CursorLoop;
            return false;
        }

        if (PageCount >= maxPages)
        {
            IncompleteReason = GlobalDealSweepIncompleteReason.PageBudgetExhausted;
            return false;
        }

        return true;
    }

    /// <summary>Marks the sweep failed after a page request or parse threw.</summary>
    public void MarkFailed() => IncompleteReason = GlobalDealSweepIncompleteReason.PageFailure;

    private void Merge(GlobalDealPage page)
    {
        foreach (var deal in page.Deals)
        {
            if (dealKeys.Add(deal.DealKey))
            {
                deals.Add(deal);
            }
        }
    }

    /// <summary>
    /// Order-independent fingerprint of one page's key set: only the identity set matters when asking
    /// "did the provider move?".
    /// </summary>
    private static string Fingerprint(GlobalDealPage page) =>
        string.Join(
            '\n',
            page.Deals.Select(deal => deal.DealKey).OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Provider bounds shared by the client, the sweep and the checks. They live in the models layer so
/// the pure sweep can validate a limit without referencing the HTTP client.
/// </summary>
public static class ItadGlobalDealLimits
{
    /// <summary>Provider cap for one <c>deals/v2</c> page.</summary>
    public const int MaxPageSize = 200;
}
