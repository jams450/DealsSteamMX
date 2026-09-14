using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

public sealed class SteamGameService(IRepository repository, ISteamStoreClient steamClient) : ISteamGameService
{
    public async Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var results = await steamClient.SearchAsync(normalized, cancellationToken);
        var observedAt = DateTime.UtcNow;

        foreach (var result in results)
        {
            var game = await repository.GetTrack<SteamGame>()
                .FirstOrDefaultAsync(g => g.AppId == result.AppId && g.Region == "mx", cancellationToken);
            if (game == null)
            {
                await repository.Save(new SteamGame
                {
                    AppId = result.AppId,
                    Name = result.Name,
                    Type = result.Type,
                    Region = "mx",
                    ObservedAt = observedAt
                });
                continue;
            }

            game.Name = result.Name;
            game.Type = result.Type;
            game.ObservedAt = observedAt;
            await repository.SaveChangesAsync();
        }

        return results;
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

        await repository.ExecuteInTransactionAsync(async () =>
        {
            var observedAt = details.ObservedAt;
            var game = await repository.GetTrack<SteamGame>()
                .FirstOrDefaultAsync(g => g.AppId == details.AppId && g.Region == details.Region, cancellationToken);
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
            return true;
        });

        return details;
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
