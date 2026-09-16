using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

public sealed class SteamGameService(IRepository repository, ISteamStoreClient steamClient) : ISteamGameService
{
    private const string Region = "mx";
    private const int MinSuggestionLength = 2;
    private const int MaxSuggestionLength = 100;
    private const int SuggestionLimit = 10;

    public async Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var results = await steamClient.SearchAsync(normalized, cancellationToken);
        var observedAt = DateTime.UtcNow;

        foreach (var result in results)
        {
            var game = await repository.GetTrack<SteamGame>()
                .FirstOrDefaultAsync(g => g.AppId == result.AppId && g.Region == Region, cancellationToken);
            if (game == null)
            {
                await repository.Save(new SteamGame
                {
                    AppId = result.AppId,
                    Name = result.Name,
                    Type = result.Type,
                    ImageUrl = result.ImageUrl,
                    Region = Region,
                    ObservedAt = observedAt
                });
                continue;
            }

            game.Name = result.Name;
            game.Type = result.Type;
            // Search artwork is the small tiny_image; never overwrite richer detail artwork already stored.
            if (string.IsNullOrWhiteSpace(game.ImageUrl) && !string.IsNullOrWhiteSpace(result.ImageUrl))
            {
                game.ImageUrl = result.ImageUrl;
            }

            game.ObservedAt = observedAt;
            await repository.SaveChangesAsync();
        }

        return results;
    }

    public async Task<IReadOnlyList<SteamSearchResult>> GetSuggestionsAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalized = query.Trim();
        if (normalized.Length > MaxSuggestionLength)
        {
            normalized = normalized[..MaxSuggestionLength];
        }

        if (normalized.Length < MinSuggestionLength)
        {
            return [];
        }

        // ToLower + Contains translates to lower()/strpos in Npgsql: case-insensitive and literal,
        // so user wildcards like % or _ are matched as text, not as LIKE patterns.
        var needle = normalized.ToLower();
        return await repository.Get<SteamGame>()
            .Where(g => g.Region == Region && g.Name.ToLower().Contains(needle))
            .OrderBy(g => g.Name)
            .Take(SuggestionLimit)
            .Select(g => new SteamSearchResult(g.AppId, g.Name, g.Type, g.ImageUrl))
            .ToListAsync(cancellationToken);
    }

    public async Task<SteamGameDetails?> GetByAppIdAsync(int appId, CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            throw new ArgumentException("AppID must be greater than zero.", nameof(appId));
        }

        var details = await steamClient.GetAppDetailsAsync(appId, cancellationToken);
        if (details == null)
        {
            return null;
        }

        string? persistedImageUrl = null;
        int? persistedLowestPriceMinor = null;
        DateTime? persistedLowestPriceAt = null;

        await repository.ExecuteInTransactionAsync(async () =>
        {
            var observedAt = details.ObservedAt;
            var game = await repository.GetTrack<SteamGame>()
                .FirstOrDefaultAsync(g => g.AppId == details.AppId && g.Region == details.Region, cancellationToken);
            // Lowest price is only comparable inside the same currency; a currency switch restarts the local low.
            var currencyChanged = game != null &&
                !string.Equals(game.Currency, details.Currency, StringComparison.OrdinalIgnoreCase);
            var priceChanged = game == null || game.Currency != details.Currency ||
                game.InitialPriceMinor != details.InitialPriceMinor ||
                game.CurrentPriceMinor != details.CurrentPriceMinor ||
                game.DiscountPercent != details.DiscountPercent;

            if (game == null)
            {
                game = new SteamGame { AppId = details.AppId, Region = details.Region };
                await repository.Save(game);
            }

            game.Name = details.Name;
            game.Type = details.Type;
            game.IsFree = details.IsFree;
            game.Currency = details.Currency;
            game.InitialPriceMinor = details.InitialPriceMinor;
            game.CurrentPriceMinor = details.CurrentPriceMinor;
            game.DiscountPercent = details.DiscountPercent;
            game.ObservedAt = observedAt;

            // Detail artwork (header_image) is richer than search tiny_image; prefer it when Steam returns one.
            if (!string.IsNullOrWhiteSpace(details.ImageUrl))
            {
                game.ImageUrl = details.ImageUrl;
            }

            if (details.CurrentPriceMinor.HasValue && !string.IsNullOrWhiteSpace(details.Currency))
            {
                var hasComparableLow = game.LowestPriceMinor.HasValue && !currencyChanged;
                if (!hasComparableLow || details.CurrentPriceMinor.Value < game.LowestPriceMinor!.Value)
                {
                    game.LowestPriceMinor = details.CurrentPriceMinor.Value;
                    game.LowestPriceAt = observedAt;
                }
            }

            if (priceChanged)
            {
                await repository.Save(new SteamPriceObservation
                {
                    SteamGameId = game.SteamGameId,
                    Currency = details.Currency,
                    InitialPriceMinor = details.InitialPriceMinor,
                    CurrentPriceMinor = details.CurrentPriceMinor,
                    DiscountPercent = details.DiscountPercent,
                    ObservedAt = observedAt
                });
            }

            await repository.SaveChangesAsync();
            persistedImageUrl = game.ImageUrl;
            persistedLowestPriceMinor = game.LowestPriceMinor;
            persistedLowestPriceAt = game.LowestPriceAt;
            return true;
        });

        return details with
        {
            ImageUrl = persistedImageUrl,
            LowestPriceMinor = persistedLowestPriceMinor,
            LowestPriceAt = persistedLowestPriceAt
        };
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is required.", nameof(query));
        }

        var normalized = query.Trim();
        if (normalized.Length > 100)
        {
            throw new ArgumentException("Search query cannot exceed 100 characters.", nameof(query));
        }

        return normalized;
    }
}
