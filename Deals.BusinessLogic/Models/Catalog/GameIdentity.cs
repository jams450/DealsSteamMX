using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Models.Catalog;

/// <summary>
/// Namespaces accepted by <c>game_external_ids</c>. The store namespaces reuse the canonical
/// <c>user_library.store</c> vocabulary verbatim, so an import seeds a mapping without translation.
/// </summary>
public static class GameExternalIdNamespaces
{
    public const string Steam = "steam";
    public const string Itad = "itad";

    /// <summary>
    /// Epic Games Store. The id is the store's own URL slug (<c>/p/&lt;slug&gt;</c>), which ITAD also accepts
    /// for shop 16 and which the store resolves back to a single offer.
    /// </summary>
    public const string Epic = "epic";

    /// <summary>
    /// Microsoft Store (Xbox). The id is the StoreId in lowercase (<c>9nkvx66j0zsk</c>), the format ITAD
    /// accepts for shop 48. The catalog answers it in uppercase and every writer normalizes it here.
    /// </summary>
    public const string Xbox = StoreKeys.Xbox;
}

/// <summary>One exact external identifier: a namespace plus its immutable id.</summary>
public sealed record GameExternalIdRef(string NamespaceName, string ExternalId);

/// <summary>
/// Resolver input. <see cref="Title"/> is the display title used only when a canonical row must be
/// created; identity always comes from the exact external ids, never from the title.
/// </summary>
public sealed record GameIdentityRequest(string Title, IReadOnlyList<GameExternalIdRef> ExternalIds);
