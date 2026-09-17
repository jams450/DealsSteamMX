using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Services.Fx;

/// <summary>Settings handed to the Banxico provider; the token never travels in the URL.</summary>
public sealed record BanxicoFxRateSettings(string Token);

/// <summary>
/// Banxico SIE series SF43718 (FIX). Only serves USD/MXN, so any other pair is reported as
/// unavailable and IFxRateService falls back to Frankfurter.
/// </summary>
public sealed class BanxicoFxRateProvider(HttpClient httpClient, BanxicoFxRateSettings settings) : IFxRateProvider
{
    private const string SeriesPath = "/SieAPIRest/service/v1/series/SF43718/datos/oportuno";
    private const string Source = "Banxico";
    private const string SupportedBase = "USD";
    private const string SupportedQuote = "MXN";
    private const string TokenHeader = "Bmx-Token";

    public async Task<FxRate> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        if (!string.Equals(baseCurrency, SupportedBase, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(quoteCurrency, SupportedQuote, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"Banxico series SF43718 only serves {SupportedBase}/{SupportedQuote}.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, SeriesPath);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(TokenHeader, settings.Token);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: the token must never reach a log line.
            throw new HttpRequestException("Banxico FX series is unavailable.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (!root.TryGetProperty("bmx", out var bmx) ||
                !bmx.TryGetProperty("series", out var series) ||
                series.ValueKind != JsonValueKind.Array ||
                series.GetArrayLength() == 0 ||
                !series[0].TryGetProperty("datos", out var datos) ||
                datos.ValueKind != JsonValueKind.Array ||
                datos.GetArrayLength() == 0)
            {
                throw new HttpRequestException("Banxico FX response is malformed.");
            }

            var latest = datos[0];
            var dateText = latest.TryGetProperty("fecha", out var fecha) && fecha.ValueKind == JsonValueKind.String
                ? fecha.GetString()
                : null;
            var rateText = latest.TryGetProperty("dato", out var dato) && dato.ValueKind == JsonValueKind.String
                ? dato.GetString()
                : null;

            // Banxico sends the rate as a string and the date as dd/MM/yyyy; "N/E" on non-business days.
            if (!DateOnly.TryParseExact(dateText, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rateDate) ||
                !decimal.TryParse(rateText, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) ||
                rate <= 0m)
            {
                throw new HttpRequestException("Banxico FX response is malformed.");
            }

            return new FxRate
            {
                Base = SupportedBase,
                Quote = SupportedQuote,
                Rate = rate,
                RateDate = rateDate,
                Source = Source,
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Banxico FX response is malformed.", ex);
        }
    }
}
