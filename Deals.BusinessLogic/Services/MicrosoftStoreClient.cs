using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Stores;

namespace Deals.BusinessLogic.Services;

/// <summary>Market, language and currency the Microsoft Store catalog is asked for.</summary>
public sealed record MicrosoftStoreClientSettings(string Market, string Languages, string Currency);

/// <summary>
/// Microsoft Store (Xbox) prices for one market, read from the public catalog lookup API. Keyless and
/// native MXN: nothing here converts currency, so every offer it produces is <c>regional</c>.
///
/// <para>
/// Identity is mandatory and is the PackageFamilyName the Playnite export already carries as the Xbox
/// <c>GameId</c>. There is deliberately no title search: measured, <c>apps.microsoft.com/api/products/
/// search</c> is title-only and answers a PackageFamilyName with unrelated products, and
/// <c>alternateId=ProductId</c> does not exist (0 results for a valid id). The lookup by
/// PackageFamilyName is the one path that resolves an identity we actually hold, and it returns the
/// canonical StoreId and the price in the same response.
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
    /// Resolves <paramref name="externalId"/> as a PackageFamilyName and returns its purchasable MXN
    /// offer. A product with no purchase availability, only zero-priced ones, or no listing in this market
    /// returns null: there is nothing comparable to show, and any previously persisted row is dropped.
    /// </summary>
    public async Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken cancellationToken)
    {
        var packageFamilyName = NormalizePackageFamilyName(externalId)
            ?? throw new ArgumentException(
                "Microsoft Store id must be a PackageFamilyName.",
                nameof(externalId));

        using var response = await GetProductAsync(packageFamilyName, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Microsoft Store catalog returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return FindFirstOffer(document.RootElement);
    }

    /// <summary>
    /// A PackageFamilyName is <c>Name_publisherHash</c>: alphanumeric, dots, dashes and exactly one
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
    /// does not exist.
    /// </summary>
    private static string BuildDealUrl(string storeId) =>
        $"https://{ProductIdHost}/detail/{storeId}";

    private StoreOffer? FindFirstOffer(JsonElement root)
    {
        // The lookup answers with "Products" (an array), not "Product": measured against the live catalog.
        if (!root.TryGetProperty("Products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        foreach (var product in products.EnumerateArray())
        {
            var offer = ToOffer(product);
            if (offer is not null)
            {
                return offer;
            }
        }

        return null;
    }

    private StoreOffer? ToOffer(JsonElement product)
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
            BuildDealUrl(storeId));
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

    private async Task<HttpResponseMessage> GetProductAsync(
        string packageFamilyName,
        CancellationToken cancellationToken)
    {
        // Both parameters are mandatory and there is no "clave=valor" form: measured, dropping either one
        // answers an empty result set rather than an error.
        var path = $"{LookupPath}?alternateId={PackageFamilyNameAlternateId}" +
            $"&value={Uri.EscapeDataString(packageFamilyName)}" +
            $"&fieldsTemplate=Details&market={settings.Market}&languages={settings.Languages}";

        using var lease = await governor.AcquireAsync(cancellationToken);

        return await httpClient.GetAsync(path, cancellationToken);
    }
}
