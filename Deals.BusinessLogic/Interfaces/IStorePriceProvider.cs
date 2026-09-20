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

    /// <summary>
    /// Respaldo por título: busca por nombre y acepta **solo** el resultado cuyo título sea el mismo después
    /// de normalizar (<see cref="Services.StoreTitleMatcher"/>), devolviendo la oferta con el id que ese
    /// resultado declare.
    ///
    /// Es el segundo camino de identidad, no el primero, y solo se usa cuando el enlace de ITAD no existe: el
    /// id que sale de aquí no lo emitió la tienda en una URL, se dedujo de un nombre, así que la coincidencia
    /// exacta es la única garantía disponible. Un no-match es preferible a un id adivinado, porque un id
    /// equivocado publica el precio de otro juego y con el aspecto de ser el correcto.
    ///
    /// Quien lo llama decide cuándo está permitido: con el proveedor de identidad degradado, no.
    /// </summary>
    Task<StoreOffer?> FindOfferByTitleAsync(string title, CancellationToken cancellationToken);
}
