namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Bounds of a store-price pass. They live with the wire model, not inside the service, so the API validates
/// the request against the same numbers the service honours.
/// </summary>
public static class LibraryStorePriceLimits
{
    /// <summary>Default pass size: one provider request per game, and the provider is rate-governed.</summary>
    public const int Default = 10;

    /// <summary>Games a single pass will ask the store about. Bigger batches are a follow-up.</summary>
    public const int Max = 50;
}

/// <summary>
/// Outcome of one store-price pass. Counts, never a list of titles.
/// <see cref="Pending"/> are library rows whose store offer is missing or past the refresh window,
/// <see cref="Unsupported"/> are those this pass cannot solve at all (no usable store id) and
/// <see cref="Rejected"/> are those where the store id belongs to a different canonical game, which means
/// the library row and the catalog disagree: no price is written rather than the wrong one.
/// </summary>
public sealed record LibraryStorePriceSyncResult(
    int Pending,
    int Unsupported,
    int Updated,
    int Failed,
    int Rejected,
    int Remaining);
