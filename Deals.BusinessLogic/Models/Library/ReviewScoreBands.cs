namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// The only source of the score bands. The label is computed on every read and never persisted, so
/// changing the thresholds relabels history without a migration.
/// </summary>
public static class ReviewScoreBands
{
    /// <summary>Label for a score, or null when unscored or out of the 0..100 range.</summary>
    public static string? Label(short? score) => score switch
    {
        null => null,
        >= 0 and <= 19 => "malo",
        >= 20 and <= 39 => "flojo",
        >= 40 and <= 59 => "regular",
        >= 60 and <= 74 => "bueno",
        >= 75 and <= 89 => "muy bueno",
        >= 90 and <= 100 => "obra maestra",
        _ => null
    };
}
