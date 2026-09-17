using System.Globalization;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Services.Fx;

/// <summary>Fallback provider: no auth, generic pair, but it lags Banxico by about a day.</summary>
public sealed class FrankfurterFxRateProvider(HttpClient httpClient) : IFxRateProvider
{
    public const string BaseUrl = "https://api.frankfurter.dev/";

    private const string RatePath = "v2/providers/banxico/rate";
    private const string Source = "Frankfurter";

    public async Task<FxRate> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        var path =
            $"{RatePath}/{Uri.EscapeDataString(baseCurrency.ToLowerInvariant())}/{Uri.EscapeDataString(quoteCurrency.ToLowerInvariant())}";

        using var response = await httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Frankfurter FX rate is unavailable.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var dateText = root.TryGetProperty("date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String
                ? dateElement.GetString()
                : null;
            var responseBase = root.TryGetProperty("base", out var baseElement) && baseElement.ValueKind == JsonValueKind.String
                ? baseElement.GetString()
                : null;
            var responseQuote = root.TryGetProperty("quote", out var quoteElement) && quoteElement.ValueKind == JsonValueKind.String
                ? quoteElement.GetString()
                : null;

            // Reject a payload for another pair instead of storing it under the requested key.
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rateDate) ||
                !string.Equals(responseBase, baseCurrency, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(responseQuote, quoteCurrency, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("rate", out var rateElement) ||
                !rateElement.TryGetDecimal(out var rate) ||
                rate <= 0m)
            {
                throw new HttpRequestException("Frankfurter FX response is malformed.");
            }

            return new FxRate
            {
                Base = baseCurrency.ToUpperInvariant(),
                Quote = quoteCurrency.ToUpperInvariant(),
                Rate = rate,
                RateDate = rateDate,
                Source = Source,
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Frankfurter FX response is malformed.", ex);
        }
    }
}
