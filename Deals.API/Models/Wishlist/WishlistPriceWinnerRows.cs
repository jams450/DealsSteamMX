namespace Deals.API.Models.Wishlist;

/// <summary>
/// One row of the wishlist price-winner query: the persisted history low and the two winners — cheapest
/// legitimate price and cheapest keyshop price — of one <c>steam_games</c> snapshot. It exists as a named type
/// (instead of an anonymous projection inside the controller) so the query the API actually runs can be
/// rendered offline with <c>ToQueryString()</c>: a projection that stops translating is a 500 in production,
/// and nothing else in the checks can catch that.
///
/// <para>
/// The direct Steam snapshot is <b>not</b> a row here; it competes for the official winner in the controller,
/// where its price and its own observation date are the same column pair. Nothing in this type decides which
/// price wins — the ordering that selects the row is part of the query.
/// </para>
/// </summary>
public sealed record WishlistPriceWinnerRow
{
    public int SteamGameId { get; init; }

    public int? HistoryLowMinor { get; init; }

    public WishlistOfferWinner? BestOfficial { get; init; }

    public WishlistOfferWinner? BestKeyshop { get; init; }
}

/// <summary>
/// The selected offer of one band, with the timestamp of the price it reports. <see cref="ObservedAt"/> is
/// <c>game_offers.observed_at</c> — when that shop's price was seen — and it is taken from the same row the
/// price and the shop metadata come from, never from the per-provider refresh stamp: those say when the
/// provider was last consulted and can be far fresher than the price on screen.
/// </summary>
public sealed record WishlistOfferWinner
{
    public int? Price { get; init; }

    public string? Source { get; init; }

    public string? Label { get; init; }

    public string? Classification { get; init; }

    public string? PricingType { get; init; }

    /// <summary>
    /// <c>game_offers.observed_at</c>: when this shop's price was seen. It is the price's timestamp, not the
    /// moment the provider was consulted — the per-provider <c>*_refreshed_at</c> stamps say the latter and are
    /// not a substitute. A shop can be consulted a minute ago and still quote a week-old price.
    /// </summary>
    public DateTime ObservedAt { get; init; }
}
