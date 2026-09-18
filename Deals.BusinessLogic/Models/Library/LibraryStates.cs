namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Canonical values for <c>user_library.state</c>, shared by the library and wishlist features.
/// </summary>
public static class LibraryStates
{
    /// <summary>Wishlist entry, written by the wishlist sync.</summary>
    public const string Wished = "wished";

    /// <summary>Bought entry imported from a store.</summary>
    public const string Owned = "owned";

    /// <summary>Subscription/catalog entry (Game Pass): never a purchase, never priced.</summary>
    public const string Subscription = "subscription";
}
