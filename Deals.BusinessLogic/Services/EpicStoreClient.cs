using System.Text;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Stores;

namespace Deals.BusinessLogic.Services;

public sealed record EpicStoreClientSettings(string Country, string Locale);

/// <summary>
/// Direct Epic Games Store price source. Keyless: it posts the same GraphQL operation the public store
/// frontend uses. Prices come back in the region's own currency (country MX answers MXN) and in minor
/// units, so nothing is converted and no FX rate is involved.
///
/// Identity is the store's own URL slug (<c>/p/&lt;slug&gt;</c>), which is also the id ITAD accepts for shop
/// 16. A search result is only accepted when one of its slug-bearing fields equals the requested slug, so
/// an edition, a DLC or a title that merely looks similar can never produce a price.
///
/// Ese "uno de sus campos" es una corrección medida, no una comodidad: Epic publica el slug en más de un
/// sitio y **no siempre coinciden**. En la mayoría de las fichas <c>urlSlug</c> lleva el slug legible
/// (<c>octopath-traveler</c>), pero en otras lleva un hash (<c>1adfedf6847a4304a920ccc3fd4a7d0f</c>) y el
/// legible solo aparece en <c>offerMappings</c> / <c>catalogNs.mappings</c>, que es justo el que llevan las
/// URLs y el que entrega ITAD. Comparar solo <c>urlSlug</c> pierde esas fichas en silencio.
/// </summary>
public sealed class EpicStoreClient(
    HttpClient httpClient,
    EpicStoreClientSettings settings,
    ProviderRequestGovernor governor)
    : IStorePriceProvider
{
    public const string StoreSource = "epic";
    public const string StoreShopName = "Epic Games Store";

    /// <summary>
    /// Host of a product page, used to build the persisted store link. The redirect chain does not always
    /// end here: it can end on the legacy <c>www.epicgames.com/store/p/&lt;slug&gt;</c> form instead, so this
    /// host is for writing links, never for accepting them.
    /// </summary>
    private const string ProductHost = "store.epicgames.com";

    /// <summary>Registrable domain the store lives on, subdomains included.</summary>
    private const string StoreDomain = "epicgames.com";

    /// <summary>Path segment that introduces a product page. Epic's own store links always carry it.</summary>
    private const string SlugPathMarker = "p";

    private const string SearchPath = "graphql";

    // Epic returns only the first ranked results, so a small window is a real risk of missing an exact
    // slug behind a generic title. 20 is the largest window observed to answer in one call.
    private const int MaxSlugLength = 64;
    private const int MaxTitleLength = 512;
    private const int MaxOfferIdLength = 64;
    private const int MaxKeywordsLength = 200;

    // Fixed, credential-free message: an exception message must never carry the payload or the URL.
    private const string MalformedPayloadMessage = "Epic Games Store returned a malformed response.";

    /// <summary>
    /// The operation the public store frontend uses. `count` is inlined as a literal: passing it as a
    /// variable means declaring its GraphQL type, and a wrong declaration fails the whole query.
    ///
    /// Se piden los cuatro campos que pueden llevar el slug: <c>urlSlug</c> y <c>productSlug</c> son los
    /// habituales, y los dos mappings son el respaldo cuando Epic devuelve el hash (ver el comentario de
    /// la clase). Pedir de más no cambia la aceptación, que sigue siendo igualdad exacta.
    /// </summary>
    private const string SearchStoreQuery = """
        query SearchStore($keywords: String!, $country: String!, $locale: String!) {
          Catalog {
            searchStore(keywords: $keywords, country: $country, locale: $locale, count: 20) {
              elements {
                title
                urlSlug
                productSlug
                id
                offerMappings {
                  pageSlug
                }
                catalogNs {
                  mappings {
                    pageSlug
                  }
                }
                price(country: $country) {
                  totalPrice {
                    discountPrice
                    originalPrice
                    currencyCode
                  }
                }
              }
            }
          }
        }
        """;

    public string Source => StoreSource;

    /// <summary>
    /// Reads the store slug out of an Epic store URL. Every accepted form carries the slug right after the
    /// store's <c>/p/</c> marker:
    /// <c>store.epicgames.com/p/octopath-traveler</c>,
    /// <c>store.epicgames.com/es-MX/p/octopath-traveler</c> and
    /// <c>www.epicgames.com/store/p/octopath-traveler</c> all return <c>octopath-traveler</c>. The host is
    /// not pinned to the canonical product host because the redirect chain lands on the legacy form for
    /// non-browser clients. Anything without a <c>/p/</c> segment returns null instead of a best guess,
    /// because a wrong slug would produce a wrong price.
    /// </summary>
    public static string? ExtractSlug(Uri storeUrl)
    {
        if (storeUrl is null ||
            !string.Equals(storeUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(storeUrl.Host.Equals(StoreDomain, StringComparison.OrdinalIgnoreCase) ||
              storeUrl.Host.EndsWith($".{StoreDomain}", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var segments = storeUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.IndexOf(segments, SlugPathMarker);
        return marker >= 0 && marker + 1 < segments.Length
            ? NormalizeSlug(segments[marker + 1])
            : null;
    }

    /// <summary>
    /// Only the exact slug the store publishes is accepted: the value is used both as a search result
    /// filter and inside a generated store URL, so anything outside the store's own slug alphabet is
    /// rejected rather than escaped.
    /// </summary>
    public static string? NormalizeSlug(string? slug)
    {
        var trimmed = slug?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxSlugLength)
        {
            return null;
        }

        return trimmed.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? trimmed.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Looks the game up in the store and returns the offer whose slug is exactly
    /// <paramref name="externalId"/>. No offer, no price or no exact match all return null: the store has
    /// nothing comparable for this game, and a previously persisted Epic offer is dropped as authoritative.
    ///
    /// Se buscan dos juegos de términos, en este orden: **las palabras del slug** y, si no hubo
    /// coincidencia, **el título guardado**. El slug va primero porque el título que guardamos está
    /// localizado — <c>steam_games.name</c> se pide con <c>cc=mx&amp;l=spanish</c> — y la búsqueda de la
    /// tienda no conoce la traducción: medido con <c>Floppy Knights</c> (appid 1057800), cuya ficha de
    /// Steam en MX se llama <c>Caballeros Floppy</c>, la búsqueda con ese título devuelve **0 elementos**
    /// y su slug <c>floppy-knights-6a735a</c> encuentra el producto. Cualquier juego cuyo nombre en Steam
    /// MX sea una traducción dependía de ese título para sobrevivir.
    ///
    /// Ampliar los términos no relaja nada: la aceptación sigue siendo igualdad estricta contra el slug,
    /// así que solo puede recuperar una coincidencia exacta que antes se perdía, nunca meter un precio
    /// ajeno. En el caso normal el primer intento acierta, así que siguen siendo las mismas peticiones.
    /// </summary>
    public async Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken cancellationToken)
    {
        var slug = NormalizeSlug(externalId)
            ?? throw new ArgumentException("Epic store slug is missing or not a valid store slug.", nameof(externalId));

        var keywords = ValidateKeywords(title);

        foreach (var attempt in KeywordAttempts(keywords, slug))
        {
            var offer = await SearchAsync(attempt, FindBySlug(slug), cancellationToken);
            if (offer is not null)
            {
                return offer;
            }
        }

        return null;
    }

    /// <summary>
    /// Respaldo por título (ver <see cref="IStorePriceProvider.FindOfferByTitleAsync"/>): busca por nombre y
    /// acepta solo el resultado que sea el mismo título, tomando de él el slug que la tienda declare.
    ///
    /// Los términos son el título tal cual, sin intentar el camino inverso (quitar el sufijo de edición,
    /// traducir, cortar en la primera palabra): cada invento de ese tipo mueve la comparación hacia el
    /// parecido, que es de donde salen los precios ajenos. Si el título guardado está en un idioma que el
    /// buscador no comparte, esto devuelve null y el juego queda sin esa tienda.
    /// </summary>
    public async Task<StoreOffer?> FindOfferByTitleAsync(string title, CancellationToken cancellationToken)
    {
        var keywords = ValidateKeywords(title);
        return await SearchAsync(keywords, FindByTitle(keywords), cancellationToken);
    }

    private static string ValidateKeywords(string? title)
    {
        var keywords = title?.Trim();
        if (string.IsNullOrEmpty(keywords) || keywords.Length > MaxKeywordsLength)
        {
            throw new ArgumentException($"Game title must be between 1 and {MaxKeywordsLength} characters.", nameof(title));
        }

        return keywords;
    }

    /// <summary>
    /// Las palabras del slug y, después, el título. El slug de la página lleva a veces un sufijo
    /// hexadecimal y la búsqueda no lo tolera: medido, <c>keywords="floppy knights 6a735a"</c> devuelve 0
    /// elementos y <c>keywords="floppy knights"</c> devuelve el producto cuyo <c>pageSlug</c> es el slug
    /// completo. Por eso el último segmento se cae cuando es un hash.
    /// </summary>
    private static IEnumerable<string> KeywordAttempts(string title, string slug)
    {
        var words = SlugWords(slug);
        if (words.Length > 0)
        {
            yield return words;
        }

        if (!string.Equals(words, title, StringComparison.OrdinalIgnoreCase))
        {
            yield return title;
        }
    }

    private static string SlugWords(string slug)
    {
        var segments = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var words = segments.Length > 1 && IsHashSegment(segments[^1]) ? segments[..^1] : segments;
        return string.Join(' ', words);
    }

    /// <summary>Un segmento hexadecimal de seis o más caracteres es el sufijo que Epic añade a la página.</summary>
    private static bool IsHashSegment(string segment)
        => segment.Length >= 6 && segment.All(char.IsAsciiHexDigit);

    private async Task<StoreOffer?> SearchAsync(
        string keywords,
        Func<JsonElement, JsonElement?> select,
        CancellationToken cancellationToken)
    {
        using var response = await PostSearchAsync(keywords, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Epic Games Store search returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var match = select(document.RootElement);
        if (match is not { } element)
        {
            return null;
        }

        // Sin slug usable no hay identidad que reclamar: el elemento se descarta en vez de escribir una oferta
        // cuya clave externa sería un valor inventado.
        return ReadSlug(element) is { } slug ? ToOffer(element, slug) : null;
    }

    /// <summary>
    /// Locates the element carrying the exact slug. <paramref name="slug"/> is validated here instead of by
    /// the caller so the comparison can never run against a value that was never a slug.
    /// </summary>
    private static Func<JsonElement, JsonElement?> FindBySlug(string slug) => root =>
    {
        foreach (var element in RequireElements(root))
        {
            if (CarriesSlug(element, slug))
            {
                return element;
            }
        }

        return null;
    };

    /// <summary>
    /// Locates the element whose title is the searched one. El título lo normaliza
    /// <see cref="StoreTitleMatcher"/> y la comparación es de igualdad: no hay puntuación de relevancia ni
    /// "el primero que se parezca", porque eso es exactamente un precio de otro juego.
    /// </summary>
    private static Func<JsonElement, JsonElement?> FindByTitle(string keywords) => root =>
    {
        foreach (var element in RequireElements(root))
        {
            if (element.ValueKind == JsonValueKind.Object &&
                StoreTitleMatcher.Matches(TryReadString(element, "title"), keywords))
            {
                return element;
            }
        }

        return null;
    };

    /// <summary>
    /// The <c>elements</c> array of a search response, or a throw when the envelope is not the expected one.
    /// A response without a <c>searchStore</c> block is malformed: read as "no results" it would look like an
    /// authoritative "this store does not sell the game" and purge the persisted snapshot.
    /// </summary>
    private static JsonElement.ArrayEnumerator RequireElements(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("Catalog", out var catalog) ||
            catalog.ValueKind != JsonValueKind.Object ||
            !catalog.TryGetProperty("searchStore", out var searchStore) ||
            searchStore.ValueKind != JsonValueKind.Object ||
            !searchStore.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        return elements.EnumerateArray();
    }

    /// <summary>
    /// El slug que este elemento declara, en orden de preferencia: los dos mappings antes que los campos
    /// sueltos, porque en las fichas donde Epic devuelve el hash en <c>urlSlug</c> es el mapping el que lleva
    /// el slug legible. Se devuelve ya normalizado, o null si el elemento no declara ningún slug usable: sin
    /// slug no hay identidad y el elemento se descarta en vez de nombrar la oferta con un valor inventado.
    /// </summary>
    private static string? ReadSlug(JsonElement element)
        => FirstMappedSlug(element, "offerMappings")
           ?? (element.TryGetProperty("catalogNs", out var catalogNamespace) &&
               catalogNamespace.ValueKind == JsonValueKind.Object
                   ? FirstMappedSlug(catalogNamespace, "mappings")
                   : null)
           ?? TrySlug(element, "productSlug")
           ?? TrySlug(element, "urlSlug");

    private static string? FirstMappedSlug(JsonElement element, string arrayName)
    {
        if (!element.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && TrySlug(entry, "pageSlug") is { } slug)
            {
                return slug;
            }
        }

        return null;
    }

    private static string? TrySlug(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeSlug(value.GetString())
            : null;

    /// <summary>
    /// ¿Este elemento declara el slug pedido? Se miran los cuatro campos que pueden llevarlo porque no
    /// significan lo mismo: en la mayoría de las fichas <c>urlSlug</c> trae el slug legible, pero en otras
    /// trae un hash y el legible queda en los mappings (ver el comentario de la clase). Todas las
    /// comparaciones son exactas contra el mismo valor normalizado: un parecido no entra.
    /// </summary>
    private static bool CarriesSlug(JsonElement element, string slug)
        => MatchesSlugField(element, "urlSlug", slug)
           || MatchesSlugField(element, "productSlug", slug)
           || MatchesMappedSlugField(element, "offerMappings", slug)
           || CarriesMappedSlug(element, slug);

    private static bool CarriesMappedSlug(JsonElement element, string slug)
        => element.TryGetProperty("catalogNs", out var catalogNamespace) &&
           catalogNamespace.ValueKind == JsonValueKind.Object &&
           MatchesMappedSlugField(catalogNamespace, "mappings", slug);

    private static bool MatchesSlugField(JsonElement element, string propertyName, string slug)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           string.Equals(NormalizeSlug(value.GetString()), slug, StringComparison.Ordinal);

    /// <summary>Arrays de mappings: cada entrada es un objeto con su propio <c>pageSlug</c>.</summary>
    private static bool MatchesMappedSlugField(JsonElement element, string arrayName, string slug)
    {
        if (!element.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && MatchesSlugField(entry, "pageSlug", slug))
            {
                return true;
            }
        }

        return false;
    }

    private StoreOffer? ToOffer(JsonElement? element, string slug)
    {
        if (element is not { } match)
        {
            return null;
        }

        var shopId = ReadRequiredString(match, "id");
        if (shopId.Length > MaxOfferIdLength)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        var title = ReadRequiredString(match, "title");
        if (title.Length > MaxTitleLength)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        // No price block or a null one means the store page is not purchasable for this country: the
        // game is listed but there is nothing to compare, so there is no offer.
        if (!match.TryGetProperty("price", out var price) || price.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (price.ValueKind != JsonValueKind.Object ||
            !price.TryGetProperty("totalPrice", out var totalPrice) ||
            totalPrice.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        // Verified against the live store: totalPrice amounts are integers already expressed in the
        // smallest unit of the currency (MXN 161.99 arrives as 16199), never as a decimal amount.
        var original = ReadRequiredMinor(totalPrice, "originalPrice");
        var current = ReadRequiredMinor(totalPrice, "discountPrice");
        var currency = ReadRequiredString(totalPrice, "currencyCode").ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        if (current > original)
        {
            // A discount above the base price is not a price: trusting it would show a bogus offer.
            throw new JsonException(MalformedPayloadMessage);
        }

        var discountPercent = original > 0 && current < original
            ? (int)decimal.Round((original - current) * 100m / original, 0, MidpointRounding.AwayFromZero)
            : (int?)null;

        return new StoreOffer(
            StoreSource,
            slug,
            shopId,
            StoreShopName,
            title,
            original,
            current,
            currency,
            discountPercent,
            BuildDealUrl(slug));
    }

    /// <summary>
    /// The store page for the matched slug, in the same locale the price was asked for. Built from the
    /// validated slug instead of echoing a provider-supplied URL, so the persisted link cannot point
    /// anywhere else.
    /// </summary>
    private string BuildDealUrl(string slug) =>
        $"https://{ProductHost}/{settings.Locale}/p/{slug}";

    private static string ReadRequiredString(JsonElement element, string property)
    {        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        return text;
    }

    /// <summary>
    /// Lectura sin contrato: un elemento del buscador que no trae título es un elemento con el que no se puede
    /// comparar, y se salta. No es un payload malformado (a diferencia de <see cref="ReadRequiredString"/>),
    /// porque el buscador devuelve elementos no relacionados a propósito y uno de ellos no puede tumbar la
    /// búsqueda entera.
    /// </summary>
    private static string? TryReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads an integer amount in minor units. A present but non-integer, negative or out-of-range amount
    /// throws instead of degrading: a silently dropped price is indistinguishable from an authoritative
    /// "no offer" and would purge the persisted snapshot.
    /// </summary>
    private static int ReadRequiredMinor(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var amount))
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        if (amount < 0 || amount > int.MaxValue)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        return (int)amount;
    }

    private async Task<HttpResponseMessage> PostSearchAsync(string keywords, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query = SearchStoreQuery,
            variables = new
            {
                keywords,
                country = settings.Country,
                locale = settings.Locale
            }
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var lease = await governor.AcquireAsync(cancellationToken);

        // Absolute path on the configured base address; POST because the operation is a query sent as a
        // body, never a query string (keywords would end up in every access log).
        return await httpClient.PostAsync(SearchPath, content, cancellationToken);
    }
}
