using System.Globalization;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// One <c>user_library</c> row projected for wishlist resolution. The pure rule filters it by user,
/// store and state, so a caller that accidentally passes every user's rows still cannot leak another
/// user's wishlist.
/// </summary>
public sealed record BundleWishlistLibraryRow(
    int UserId,
    string Store,
    string State,
    string StoreGameId);

/// <summary>
/// One <c>steam_games</c> row projected for wishlist resolution. The ITAD id is read from here, never
/// from <c>user_library.itad_game_id</c>, because that column is not maintained and would be a stale
/// identity.
/// </summary>
public sealed record BundleWishlistGameRow(
    int AppId,
    string Name,
    string? ItadGameId,
    string Region);

/// <summary>
/// The resolved wishlist identity used for exact bundle matching: the Steam appid, the catalog name and
/// the MX catalog ITAD game id (null when the game has no ITAD identity).
/// </summary>
public sealed record BundleWishlistEntry(
    int AppId,
    string Name,
    string? ItadGameId);

/// <summary>
/// The wishlist rules of the bundle catalog, as pure functions so they are exercised without a database.
/// Resolution goes <c>user_library</c> (store steam, state wished) -&gt; Steam appid -&gt;
/// <c>steam_games</c> region MX -&gt; ITAD game id; matching is exact by tier item UUID, with the
/// bundle's own linked appid as a proven-identity fallback. Titles are never compared.
/// </summary>
public static class BundleWishlist
{
    public const string SteamStore = "steam";
    public const string WishedState = "wished";
    public const string Region = "mx";

    /// <summary>
    /// Resolves the current user's wished Steam games that exist in the MX catalog. Rows of any other
    /// user, store or state are dropped, and an appid with no MX catalog row is not an identity (so it
    /// can never accidentally match).
    /// </summary>
    public static IReadOnlyList<BundleWishlistEntry> Resolve(
        int currentUserId,
        IEnumerable<BundleWishlistLibraryRow> libraryRows,
        IEnumerable<BundleWishlistGameRow> games)
    {
        var appIds = libraryRows
            .Where(row => row.UserId == currentUserId &&
                string.Equals(row.Store, SteamStore, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.State, WishedState, StringComparison.OrdinalIgnoreCase))
            .Select(row => ParseAppId(row.StoreGameId))
            .Where(appId => appId > 0)
            .ToHashSet();

        if (appIds.Count == 0)
        {
            return [];
        }

        var byAppId = games
            .Where(game => string.Equals(game.Region, Region, StringComparison.OrdinalIgnoreCase))
            .GroupBy(game => game.AppId)
            .ToDictionary(group => group.Key, group => group.First());

        return appIds
            .Where(byAppId.ContainsKey)
            .OrderBy(appId => appId)
            .Select(appId => new BundleWishlistEntry(appId, byAppId[appId].Name, byAppId[appId].ItadGameId))
            .ToList();
    }

    /// <summary>
    /// Returns the wishlist games the bundle provably contains, matched by the exact tier item UUID or,
    /// only as a fallback, by the bundle's own linked Steam appid (both are proven identities). A
    /// same-title/different-ID item never matches, and the returned name is the wishlist game's name.
    /// </summary>
    public static IReadOnlyList<BundleCatalogWishlistMatch> Match(
        IEnumerable<BundleWishlistEntry> wishlist,
        IReadOnlyList<SteamGameBundleTier> tiers,
        IReadOnlyList<BundleCatalogMember> linkedGames)
    {
        var tierItadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in tiers.SelectMany(tier => tier.Games ?? []))
        {
            if (!string.IsNullOrWhiteSpace(game.ItadGameId))
            {
                tierItadIds.Add(game.ItadGameId);
            }
        }

        var linkedAppIds = linkedGames.Select(game => game.AppId).ToHashSet();
        var matches = new List<BundleCatalogWishlistMatch>();
        var seen = new HashSet<int>();
        foreach (var entry in wishlist)
        {
            var matched =
                (!string.IsNullOrWhiteSpace(entry.ItadGameId) && tierItadIds.Contains(entry.ItadGameId)) ||
                linkedAppIds.Contains(entry.AppId);
            if (matched && seen.Add(entry.AppId))
            {
                matches.Add(new BundleCatalogWishlistMatch(entry.AppId, entry.Name));
            }
        }

        return matches;
    }

    public static int ParseAppId(string storeGameId) =>
        int.TryParse(storeGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : 0;
}
