namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Canonical store keys, the single source of truth shared by the library import and reviews.
/// Reviews use the same vocabulary as <c>user_library.store</c>, so a review and a library row join by
/// plain text without translation.
/// </summary>
public static class StoreKeys
{
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Gog = "gog";
    public const string Xbox = "xbox";
    public const string Amazon = "amazon";
    public const string Ubisoft = "ubisoft";
    public const string Humble = "humble";
    public const string Battlenet = "battlenet";

    private static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Steam, Epic, Gog, Xbox, Amazon, Ubisoft, Humble, Battlenet
    };

    /// <summary>True when the value is one of the eight canonical store keys (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}
