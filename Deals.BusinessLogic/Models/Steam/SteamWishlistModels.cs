namespace Deals.BusinessLogic.Models.Steam;

/// <summary>
/// Outcome of one wishlist fetch. <see cref="Ok"/> with an empty list is a legitimate empty wishlist;
/// <see cref="Inaccessible"/> is the private (or unknown) profile the API answers with <c>x-eresult 15</c>;
/// <see cref="RateLimited"/> means back off without touching persisted state; <see cref="Failed"/> covers
/// every other degradation (transport, malformed payload, unknown result code).
/// </summary>
public enum SteamWishlistStatus
{
    Ok,
    Inaccessible,
    RateLimited,
    Failed
}

/// <summary>One Steam wishlist entry. <see cref="AddedAt"/> is the UTC conversion of <c>date_added</c>.</summary>
public sealed record SteamWishlistItem(int AppId, int? Priority, DateTimeOffset? AddedAt);

/// <summary>
/// Typed result of a fetch: the client never throws for odd payload data, because an exception must not
/// be able to purge the persisted wishlist.
/// </summary>
public sealed record SteamWishlistResult(SteamWishlistStatus Status, IReadOnlyList<SteamWishlistItem> Items)
{
    public static SteamWishlistResult Ok(IReadOnlyList<SteamWishlistItem> items) => new(SteamWishlistStatus.Ok, items);
    public static SteamWishlistResult Inaccessible() => new(SteamWishlistStatus.Inaccessible, []);
    public static SteamWishlistResult RateLimited() => new(SteamWishlistStatus.RateLimited, []);
    public static SteamWishlistResult Failed() => new(SteamWishlistStatus.Failed, []);
}

/// <summary>
/// Wishlist states as persisted in <c>users.wishlist_state</c> and as composed for the API.
/// </summary>
public static class WishlistStates
{
    public const string Ok = "ok";
    public const string Inaccessible = "inaccessible";
    public const string NeverSynced = "never_synced";
    public const string NoSteamId = "no_steam_id";

    /// <summary>
    /// Only the outcome of the last sync attempt ("ok" | "inaccessible") is persisted; never-synced and
    /// no-steam-id are derived so the UI can tell "no account linked" from "linked but never synced".
    /// </summary>
    public static string Compose(string? steamId64, DateTime? syncedAt, string? wishlistState)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
        {
            return NoSteamId;
        }

        if (syncedAt is null)
        {
            return NeverSynced;
        }

        return string.IsNullOrWhiteSpace(wishlistState) ? Ok : wishlistState;
    }
}

/// <summary>
/// Result of the fast list snapshot: counters plus the state of the last user processed. FetchedFromSteam /
/// FetchFailed count the appids the list pulled from Steam because no local row existed yet.
/// </summary>
public sealed record WishlistListSyncReport(
    string State,
    int ItemCount,
    int Added,
    int Updated,
    int Removed,
    DateTime? SyncedAt,
    int FetchedFromSteam = 0,
    int FetchFailed = 0);

/// <summary>Result of the paced per-game price refresh.</summary>
public sealed record WishlistRefreshReport(int Refreshed, int Failed);
