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
/// 16. A search result is only accepted when its <c>urlSlug</c> equals the requested slug, so an edition,
/// a DLC or a title that merely looks similar can never produce a price.
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
    /// </summary>
    private const string SearchStoreQuery = """
        query SearchStore($keywords: String!, $country: String!, $locale: String!) {
          Catalog {
            searchStore(keywords: $keywords, country: $country, locale: $locale, count: 20) {
              elements {
                title
                urlSlug
                id
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
    /// Looks the game up by title and returns the offer whose <c>urlSlug</c> is exactly
    /// <paramref name="externalId"/>. No offer, no price or no exact match all return null: the store has
    /// nothing comparable for this game, and a previously persisted Epic offer is dropped as authoritative.
    /// </summary>
    public async Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken cancellationToken)
    {
        var slug = NormalizeSlug(externalId)
            ?? throw new ArgumentException("Epic store slug is missing or not a valid store slug.", nameof(externalId));

        var keywords = title?.Trim();
        if (string.IsNullOrEmpty(keywords) || keywords.Length > MaxKeywordsLength)
        {
            throw new ArgumentException($"Game title must be between 1 and {MaxKeywordsLength} characters.", nameof(title));
        }

        using var response = await PostSearchAsync(keywords, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Epic Games Store search returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ToOffer(FindElement(document.RootElement, slug), slug);
    }

    /// <summary>
    /// Locates the element carrying the exact slug. A response with no <c>searchStore</c> block is
    /// malformed (it would look like an authoritative "no offer" and purge the snapshot); an empty or
    /// unrelated <c>elements</c> list is an honest no-match.
    /// </summary>
    private static JsonElement? FindElement(JsonElement root, string slug)
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

        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (element.TryGetProperty("urlSlug", out var slugValue) &&
                slugValue.ValueKind == JsonValueKind.String &&
                string.Equals(NormalizeSlug(slugValue.GetString()), slug, StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
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
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
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
