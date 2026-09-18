namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Values of the read-only <c>priceState</c> returned by the library endpoint. The state is computed on
/// every read and never persisted: a title candidate never creates identity.
/// </summary>
public static class LibraryPriceStates
{
    /// <summary>Exact identity (Steam appid or ITAD uuid) reached a steam_games snapshot.</summary>
    public const string Exact = "exact";

    /// <summary>Single normalized-title candidate. Display-only; never ownership.</summary>
    public const string TitleCandidate = "title_candidate";

    /// <summary>No usable price binding.</summary>
    public const string None = "none";

    /// <summary>Subscription/catalog entry (Game Pass): never priced.</summary>
    public const string Subscription = "subscription";
}

/// <summary>Origin of a resolved binding, mirroring the exact-id namespaces plus the title heuristic.</summary>
public static class LibraryBindingSources
{
    public const string Steam = "steam";
    public const string Itad = "itad";
    public const string Title = "title";
}

/// <summary>
/// Immutable price binding for one library row. All price amounts are MXN minor units, or null when no
/// converted price exists. Never carries ownership: the state says how the price was reached, not what
/// the user owns.
/// </summary>
public sealed record LibraryPriceBinding(
    string PriceState,
    string? BindingSource,
    int? SteamAppId,
    int? BestOfficialMinor,
    int? BestKeyshopMinor,
    int? HistoryLowMinor,
    int? BasePriceMinor,
    string? BaseCurrency)
{
    /// <summary>No binding at all: every field empty.</summary>
    public static readonly LibraryPriceBinding None =
        new(LibraryPriceStates.None, null, null, null, null, null, null, null);

    /// <summary>Game Pass/subscription: a real state, never a price lookup.</summary>
    public static readonly LibraryPriceBinding Subscription =
        new(LibraryPriceStates.Subscription, null, null, null, null, null, null, null);
}
