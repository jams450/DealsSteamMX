using Deals.BusinessLogic.Models.Stores;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// One direct store price source. Implementations are single-store, keyless where the store allows it, and
/// always answer in the store's regional currency (MXN for country MX), so an offer from here is
/// <c>pricing_type = 'regional'</c> and never an FX estimate.
///
/// A provider failure degrades to "no offer from this store": it must never fail the game page or cancel
/// another provider, because every implementation shares one request budget
/// (<see cref="Services.ProviderRequestGovernor"/>).
/// </summary>
public interface IStorePriceProvider
{
    /// <summary>Value written to <c>game_offers.source</c>, e.g. <c>epic</c>.</summary>
    string Source { get; }

    /// <summary>
    /// Looks a game up in this store by title and returns its offer, or null when the store does not sell it
    /// or no result matches. <paramref name="externalId"/> is the id already known for this store and is
    /// mandatory: a result is only accepted when it carries that exact id, so a title that merely looks
    /// similar never produces an offer.
    /// </summary>
    Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken cancellationToken);
}
