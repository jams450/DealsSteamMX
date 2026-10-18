using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Deals.BusinessLogic.Models.GgDeals;

/// <summary>
/// One game listed inside a gg.deals bundle tier. The provider documents only <c>title</c> and
/// <c>url</c>; no Steam appid, DRM, vendor or standalone price is invented here. The URL is kept as
/// the only corroborable identity hint and may be null when the provider published none.
/// </summary>
public sealed record GgDealsBundleItem(string Title, string? Url);

/// <summary>
/// One tier of a gg.deals bundle: its own price, its currency, the number of games the buyer may pick
/// and the games the provider listed. <see cref="GamesCount"/> is the provider's "choose N of M" rule
/// (null means every listed game is included); it is never treated as the number of items listed. The
/// price is an exact decimal string parsed at the client boundary, never a float.
/// </summary>
public sealed record GgDealsBundleTier(
    int? PriceMinor,
    string? Currency,
    int? GamesCount,
    IReadOnlyList<GgDealsBundleItem> Games);

/// <summary>
/// One active gg.deals bundle as returned by the documented <c>active/</c> endpoint. Display data only:
/// a bundle is not an offer and never takes part in the price comparison. <see cref="DateFrom"/> and
/// <see cref="DateTo"/> are read explicitly as UTC; <see cref="Url"/> is the nullable GG.deals article
/// link and is valid as null. <see cref="BundleKey"/> is the client-owned heuristic documented on
/// <see cref="GgDealsBundleKey"/>, not a vendor identifier.
/// </summary>
public sealed record GgDealsBundle(
    string BundleKey,
    string Title,
    string? Url,
    DateTime DateFrom,
    DateTime? DateTo,
    IReadOnlyList<GgDealsBundleTier> Tiers);

/// <summary>
/// One page of the documented <c>active/</c> catalog. <see cref="NextCursor"/> is the validated,
/// opaque <c>next</c> link to request next (null ends the catalog) and <see cref="TotalCount"/> is the
/// provider's own total for the whole enumeration.
/// </summary>
public sealed record GgDealsBundlePage(
    IReadOnlyList<GgDealsBundle> Bundles,
    int TotalCount,
    string? NextCursor);

/// <summary>
/// The client-owned identity heuristic for a gg.deals bundle. GG.deals documents no unique bundle id,
/// its <c>url</c> is nullable and the same title can legitimately repeat, so the key is derived from
/// provider fields (title, start date and the tier price/currency/selection summary) and hashed.
///
/// It is deliberately prefixed so it can never collide with an ITAD UUID or shop key: the catalog
/// groups snapshots by source family, and the prefix is a second, independent guard. Changing the
/// derivation changes every key, so it is versioned in the prefix (<c>gg1:</c>).
/// </summary>
public static class GgDealsBundleKey
{
    /// <summary>Versioned prefix; keeps the heuristic out of every other source's key space.</summary>
    public const string Prefix = "gg:";

    private const int HashLength = 40;

    public static string Compute(string title, DateTime dateFromUtc, IReadOnlyList<GgDealsBundleTier> tiers)
    {
        var tierSummary = string.Join(
            '|',
            tiers.Select(tier => string.Create(
                CultureInfo.InvariantCulture,
                $"{tier.PriceMinor?.ToString(CultureInfo.InvariantCulture) ?? "-"}:{tier.Currency ?? "-"}:{tier.GamesCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}:{tier.Games.Count.ToString(CultureInfo.InvariantCulture)}")));

        var canonical = string.Join(
            '\n',
            Normalize(title),
            dateFromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            tierSummary);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Prefix + hex[..HashLength];
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
