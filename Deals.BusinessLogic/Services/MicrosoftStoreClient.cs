using System.Net;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Stores;

namespace Deals.BusinessLogic.Services;

/// <summary>Market, language and currency the Microsoft Store catalog is asked for.</summary>
public sealed record MicrosoftStoreClientSettings(string Market, string Languages, string Currency);

/// <summary>
/// Microsoft Store (Xbox) prices for one market, read from the public catalog API. Keyless and native MXN:
/// nothing here converts currency, so every offer it produces is <c>regional</c>.
///
/// <para>
/// Identity is mandatory and comes in two measured shapes, resolved by two endpoints of the same catalog:
/// the PackageFamilyName a Playnite Xbox row carries (<c>products/lookup?alternateId=PackageFamilyName</c>)
/// and the 12-character StoreId a Microsoft store URL carries (<c>products/{StoreId}</c>, which answers the
/// canonical id and the price just the same). There is deliberately no title search: measured,
/// <c>apps.microsoft.com/api/products/search</c> is title-only and answers a PackageFamilyName with
/// unrelated products, and <c>alternateId=ProductId</c> does not exist (0 results for a valid id).
/// </para>
/// </summary>
public sealed class MicrosoftStoreClient(
    HttpClient httpClient,
    MicrosoftStoreClientSettings settings,
    ProviderRequestGovernor governor) : IStorePriceProvider
{
    public const string StoreSource = "microsoft";
    public const string StoreShopName = "Microsoft Store";

    private const string ProductIdHost = "apps.microsoft.com";

    /// <summary>
    /// Buscador de la tienda. Es **otro host** que el del catálogo (<c>displaycatalog.mp.microsoft.com</c>),
    /// así que va como URL absoluta y no como ruta sobre la base configurada. Se deja como constante y no como
    /// opción porque nadie lo va a configurar: el host es el de la tienda y cambiarlo sería otro proveedor.
    /// </summary>
    private const string SearchUrl = "https://apps.microsoft.com/api/products/search";

    /// <summary>
    /// La lista de resultados del buscador viene en <c>productsList</c>. Medido: las claves <c>Products</c> y
    /// <c>products</c> **no existen** en esta respuesta, así que leerlas devuelve cero resultados sin ningún
    /// error — la peor forma de fallar, porque se lee igual que "la tienda no lo vende".
    /// </summary>
    private const string SearchResultsProperty = "productsList";

    /// <summary>
    /// Marca de producto de juego en el buscador. El buscador devuelve también aplicaciones, temas y
    /// películas, y este camino solo puede escribir precios de juegos.
    /// </summary>
    private const string GameFlagProperty = "isGame";

    /// <summary>Path of the catalog lookup, relative to the configured base address.</summary>
    private const string LookupPath = "v7.0/products/lookup";

    private const string PackageFamilyNameAlternateId = "PackageFamilyName";

    /// <summary>Actions on an availability that mean "a user can buy this right now".</summary>
    private const string PurchaseAction = "Purchase";

    private const string MalformedPayloadMessage = "Microsoft Store returned a malformed response.";

    /// <summary>Store ids are 12 alphanumeric characters (<c>9NKVX66J0ZSK</c>).</summary>
    private const int StoreIdLength = 12;

    /// <summary>PackageFamilyName is <c>Name_publisherHash</c>; the catalog accepts up to 255.</summary>
    private const int MaxPackageFamilyNameLength = 255;

    private const int MaxTitleLength = 512;

    private const int MaxStoreIdLength = 64;

    public string Source => StoreSource;

    /// <summary>
    /// Resolves <paramref name="externalId"/> and returns its purchasable MXN offer. Two id shapes are
    /// accepted because the store hands out two kinds: the PackageFamilyName a Playnite Xbox row carries
    /// (looked up by alternate id) and the 12-character StoreId a Microsoft store URL carries (read
    /// directly). A product with no purchase availability, only zero-priced ones, or no listing in this
    /// market returns null: there is nothing comparable to show, and any previously persisted row is dropped.
    /// </summary>
    public Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken cancellationToken) =>
        FindOfferAsync(title, externalId, cancellationToken, dealUrl: null);

    /// <summary>
    /// Respaldo por título (ver <see cref="IStorePriceProvider.FindOfferByTitleAsync"/>): busca en la tienda,
    /// se queda con el resultado que sea el mismo juego y reusa el camino normal con el StoreId que ese
    /// resultado trae. El precio sale del catálogo igual que siempre, así que las reglas de qué es un precio
    /// (acción <c>Purchase</c>, moneda del mercado, SKU con importe) no se duplican ni se relajan.
    ///
    /// Los dos filtros son duros y por motivos distintos: <c>isGame</c> deja fuera lo que no es un juego, y la
    /// igualdad de título deja fuera los juegos que no son **este** juego. Cualquier cosa menos que eso es un
    /// id adivinado.
    ///
    /// Nota medida: el catálogo de Microsoft **sí** está localizado, así que aquí el título guardado (que se
    /// pide con <c>cc=mx&amp;l=spanish</c>) es un término de búsqueda válido, al revés que en Epic.
    /// </summary>
    public async Task<StoreOffer?> FindOfferByTitleAsync(string title, CancellationToken cancellationToken)
    {
        var query = title?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > MaxTitleLength)
        {
            return null;
        }

        var url = $"{SearchUrl}?query={Uri.EscapeDataString(query)}" +
            $"&hl={Uri.EscapeDataString(settings.Languages)}&gl={Uri.EscapeDataString(settings.Market)}";

        using var response = await GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Microsoft Store search returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (ReadStoreIdFromSearch(document.RootElement, query) is not { } storeId)
        {
            return null;
        }

        return await FindOfferAsync(query, storeId, cancellationToken, BuildDealUrl(storeId));
    }

    /// <summary>
    /// El StoreId del primer resultado que sea un juego con el mismo título. La ausencia de
    /// <c>productsList</c> es un payload malformado (leída como lista vacía se confundiría con "no lo vende" y
    /// borraría la oferta guardada); una lista sin coincidencia es un no-match honesto.
    /// </summary>
    private static string? ReadStoreIdFromSearch(JsonElement root, string query)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(SearchResultsProperty, out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        foreach (var result in results.EnumerateArray())
        {
            // Cada filtro en su propia guarda: encadenados en un solo `||` la precedencia deja pasar un
            // resultado que **no trae** el flag, y ese resultado podría no ser un juego.
            if (result.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!result.TryGetProperty(GameFlagProperty, out var isGame) || isGame.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            if (!StoreTitleMatcher.Matches(ReadOptionalString(result, "title"), query))
            {
                continue;
            }

            // El id se valida con la misma regla que el que viene de una URL: el buscador devuelve un
            // productId que tiene que ser un StoreId usable antes de que nadie lo persista como identidad.
            if (NormalizeStoreId(ReadOptionalString(result, "productId")) is { } storeId)
            {
                return storeId;
            }
        }

        return null;
    }

    /// <summary>
    /// The same lookup with an explicit offer link, for when the id was read out of the store's own URL and
    /// that URL is better than one rebuilt from the id. The interface has no such parameter: a caller that
    /// only knows the id keeps using it.
    /// </summary>
    public async Task<StoreOffer?> FindOfferAsync(
        string title,
        string externalId,
        CancellationToken cancellationToken,
        string? dealUrl)
    {
        var path = BuildLookupPath(externalId);

        using var response = await GetAsync(path, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Authoritative: the store says this product does not exist. Not a failure, and retrying it on
            // every request would only re-ask a question already answered.
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Microsoft Store catalog returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return FindFirstOffer(document.RootElement, dealUrl);
    }

    /// <summary>
    /// The StoreId in a Microsoft store URL. ITAD's deal link lands on whichever URL shape the store
    /// chooses — measured shapes include <c>apps.microsoft.com/detail/{id}</c>,
    /// <c>www.microsoft.com/store/productId/{id}</c> and <c>xbox.com/{locale}/games/store/{slug}/{id}</c> —
    /// so the id is taken as the last path segment and validated as a StoreId, the same way Epic's
    /// extractor anchors on the slug segment instead of on a whole URL. The host is checked first so a page
    /// that merely imitates a Microsoft host cannot hand us an id.
    /// </summary>
    public static string? ExtractStoreId(Uri? url)
    {
        if (url is null || !IsMicrosoftHost(url.Host))
        {
            return null;
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : NormalizeStoreId(Uri.UnescapeDataString(segments[^1]));
    }

    private static bool IsMicrosoftHost(string host) =>
        host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("xbox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".xbox.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Path for the id this call was given, or a throw when it is neither shape: sending a wrong kind of id
    /// would answer an empty result set, which is indistinguishable from "not sold here".
    /// </summary>
    private string BuildLookupPath(string externalId)
    {
        var query = $"market={settings.Market}&languages={settings.Languages}&fieldsTemplate=Details";

        if (NormalizePackageFamilyName(externalId) is { } packageFamilyName)
        {
            return $"{LookupPath}?alternateId={PackageFamilyNameAlternateId}" +
                $"&value={Uri.EscapeDataString(packageFamilyName)}&{query}";
        }

        if (NormalizeStoreId(externalId) is { } storeId)
        {
            return $"v7.0/products/{storeId}?{query}";
        }

        throw new ArgumentException(
            "Microsoft Store id must be a PackageFamilyName or a StoreId.",
            nameof(externalId));
    }

    /// <summary>
    /// True when <paramref name="value"/> is an id this client can actually resolve. An id stored under a
    /// shape the catalog does not accept is not an identity: it is ignored and the id is derived again,
    /// instead of being sent as a query that would answer nothing.
    /// </summary>
    public static bool IsUsableId(string? value) =>
        NormalizePackageFamilyName(value) is not null || NormalizeStoreId(value) is not null;

    /// <summary>A PackageFamilyName is <c>Name_publisherHash</c>: alphanumeric, dots, dashes and exactly one
    /// underscore. Anything else (a StoreId, a display name, a URL) is rejected instead of being sent as a
    /// lookup that would silently answer nothing.
    /// </summary>
    public static string? NormalizePackageFamilyName(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxPackageFamilyNameLength)
        {
            return null;
        }

        var underscore = trimmed.IndexOf('_');
        if (underscore <= 0 || underscore != trimmed.LastIndexOf('_') || underscore == trimmed.Length - 1)
        {
            return null;
        }

        return trimmed.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? trimmed
            : null;
    }

    /// <summary>Lowercase StoreId, the format the canonical catalog and ITAD both use.</summary>
    public static string? NormalizeStoreId(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length != StoreIdLength || !trimmed.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The product page for a StoreId. Built from the validated id instead of echoing a provider URL, and
    /// verified to resolve: this link redirects to the canonical locale URL and answers 410 for an id that
    /// does not exist. Used when nothing better exists — see <c>dealUrl</c> below.
    /// </summary>
    private static string BuildDealUrl(string storeId) =>
        $"https://{ProductIdHost}/detail/{storeId}";

    /// <summary>
    /// The locale store page without its tracking query, e.g.
    /// <c>https://www.xbox.com/es-MX/games/store/octopath-traveler/9n9606cc950j</c>. Measured: this is what
    /// ITAD's Microsoft deal link lands on, so when the id was read out of that link the link itself is the
    /// better offer URL — it carries the slug and the Spanish locale, where <see cref="BuildDealUrl"/> only
    /// carries the id. Both resolve; this one is what a buyer would share.
    /// </summary>
    public static string StorePageUrl(Uri url) => url.GetLeftPart(UriPartial.Path);

    private StoreOffer? FindFirstOffer(JsonElement root, string? dealUrl)
    {
        // Measured: the alternate-id lookup answers "Products" (an array) and the direct product call
        // answers "Product" (a single object). The payload inside is the same, only the envelope differs.
        if (root.TryGetProperty("Products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var product in products.EnumerateArray())
            {
                if (ToOffer(product, dealUrl) is { } offer)
                {
                    return offer;
                }
            }

            return null;
        }

        if (root.TryGetProperty("Product", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            return ToOffer(single, dealUrl);
        }

        throw new JsonException(MalformedPayloadMessage);
    }

    private StoreOffer? ToOffer(JsonElement product, string? dealUrl)
    {
        if (product.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        var storeId = NormalizeStoreId(ReadRequiredString(product, "ProductId"))
            ?? throw new JsonException(MalformedPayloadMessage);

        var title = ReadTitle(product);
        var preferredSkuId = ReadOptionalString(product, "PreferredSkuId");

        if (!product.TryGetProperty("DisplaySkuAvailabilities", out var skus) ||
            skus.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        decimal? regular = null;
        decimal? current = null;

        foreach (var sku in skus.EnumerateArray())
        {
            // The store's own pick for what a buyer should get. When it is absent the SKUs are all
            // considered and the cheapest purchasable one wins, which is still a real price.
            if (preferredSkuId is not null && sku.TryGetProperty("Sku", out var skuNode) &&
                ReadOptionalString(skuNode, "SkuId") is { } skuId &&
                !string.Equals(skuId, preferredSkuId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!sku.TryGetProperty("Availabilities", out var availabilities) ||
                availabilities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var availability in availabilities.EnumerateArray())
            {
                if (!IsPurchasable(availability, out var listPrice, out var msrp))
                {
                    continue;
                }

                // A subscription (Game Pass) product carries extra Purchase availabilities priced at 0.0
                // next to the real one, so the zero rows are skipped rather than read as "free".
                if (listPrice <= 0)
                {
                    continue;
                }

                if (current is null || listPrice < current)
                {
                    current = listPrice;
                    regular = msrp;
                }
            }
        }

        if (current is null || regular is null)
        {
            // No purchasable price in this market. Not an error: the product exists but is not for sale here.
            return null;
        }

        var original = ToMinorUnits(regular.Value);
        var discounted = ToMinorUnits(current.Value);

        var discountPercent = original > 0 && discounted < original
            ? (int)decimal.Round((original - discounted) * 100m / original, 0, MidpointRounding.AwayFromZero)
            : (int?)null;

        return new StoreOffer(
            StoreSource,
            storeId,
            storeId,
            StoreShopName,
            title,
            original,
            discounted,
            settings.Currency,
            discountPercent,
            dealUrl ?? BuildDealUrl(storeId));
    }

    /// <summary>
    /// True when the availability is a user purchase with a numeric price in the configured currency.
    /// <c>CurrencyCode</c> is checked because the payload also carries zero-priced licence rows in other
    /// currencies for the same SKU.
    /// </summary>
    private bool IsPurchasable(
        JsonElement availability,
        out decimal listPrice,
        out decimal msrp)
    {
        listPrice = 0m;
        msrp = 0m;

        if (availability.ValueKind != JsonValueKind.Object ||
            !availability.TryGetProperty("Actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Array ||
            !actions.EnumerateArray().Any(action =>
                action.ValueKind == JsonValueKind.String &&
                string.Equals(action.GetString(), PurchaseAction, StringComparison.Ordinal)) ||
            !availability.TryGetProperty("OrderManagementData", out var order) ||
            order.ValueKind != JsonValueKind.Object ||
            !order.TryGetProperty("Price", out var priceNode) ||
            priceNode.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!string.Equals(
                ReadOptionalString(priceNode, "CurrencyCode"),
                settings.Currency,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryReadDecimal(priceNode, "ListPrice", out listPrice))
        {
            return false;
        }

        // MSRP is the struck-through price. When it is absent the listing price is the regular one, so a
        // product without a discount still reports a truthful pair instead of a bogus 100% off.
        msrp = TryReadDecimal(priceNode, "MSRP", out var msrpValue) && msrpValue >= listPrice
            ? msrpValue
            : listPrice;

        return true;
    }

    /// <summary>
    /// The catalog bills decimals (419.7), unlike Epic, which bills minor-unit integers (16199). This is
    /// the single place that converts, so no caller has to know which store it is talking to.
    /// </summary>
    private static int ToMinorUnits(decimal amount) =>
        (int)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static string ReadTitle(JsonElement product)
    {
        if (!product.TryGetProperty("LocalizedProperties", out var localized) ||
            localized.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        foreach (var entry in localized.EnumerateArray())
        {
            if (ReadOptionalString(entry, "ProductTitle") is { } title && title.Length <= MaxTitleLength)
            {
                return title;
            }
        }

        throw new JsonException(MalformedPayloadMessage);
    }

    private static string ReadRequiredString(JsonElement element, string property) =>
        ReadOptionalString(element, property) ?? throw new JsonException(MalformedPayloadMessage);

    private static string? ReadOptionalString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool TryReadDecimal(JsonElement element, string property, out decimal amount)
    {
        amount = 0m;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var parsed))
        {
            return false;
        }

        // A negative or unrepresentable amount is not a price. Out-of-range throws instead of degrading:
        // a silently dropped price is indistinguishable from an authoritative "no offer".
        if (parsed < 0m || parsed > int.MaxValue / 100m)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        amount = parsed;
        return true;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var lease = await governor.AcquireAsync(cancellationToken);

        return await httpClient.GetAsync(path, cancellationToken);
    }
}
