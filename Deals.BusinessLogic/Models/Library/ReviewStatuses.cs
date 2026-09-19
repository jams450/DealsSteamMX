namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// How a playthrough ended, stored on each review. The single source of truth shared by the review service
/// and the API. "Por jugar" has no constant on purpose: it is the ABSENCE of a review, derived on read, so
/// it can never be persisted as a status by mistake.
/// </summary>
public static class ReviewStatuses
{
    /// <summary>Terminado: se acabó la historia principal.</summary>
    public const string Finished = "finished";

    /// <summary>Completado 100%: terminado y además completado del todo.</summary>
    public const string Completed = "completed";

    /// <summary>Dropeado: se abandonó sin terminar.</summary>
    public const string Dropped = "dropped";

    private static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Finished, Completed, Dropped
    };

    /// <summary>True when the value is one of the three stored statuses (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}
