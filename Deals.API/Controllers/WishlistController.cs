using System.Globalization;
using System.Security.Claims;
using Deals.API.Models.Wishlist;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminWithId")]
public class WishlistController : ControllerBase
{
    private const string SteamStore = "steam";
    private const string WishedState = "wished";

    // Same region the rest of the code reads and writes.
    private const string Region = "mx";

    // game_offers bands. "keyshop" is the grey-market band; every other classification is a legitimate
    // shop (ITAD's official allowlist and gg.deals retail, which lands as "authorized").
    private const string KeyshopClassification = "keyshop";

    // An offer without a derived MXN amount cannot take part in an MXN comparison.
    private const string UnconvertedPricing = "unconverted";

    private const string MxnCurrency = "MXN";

    private readonly IRepository _repository;
    private readonly IWishlistSyncService _wishlistSyncService;
    private readonly JobRunLog _jobRunLog;

    public WishlistController(IRepository repository, IWishlistSyncService wishlistSyncService, JobRunLog jobRunLog)
    {
        _repository = repository;
        _wishlistSyncService = wishlistSyncService;
        _jobRunLog = jobRunLog;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var user = await _repository.Get<User>()
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        var state = WishlistStates.Compose(user.SteamId64, user.WishlistSyncedAt, user.WishlistState);
        var items = await LoadItemsAsync(userId, cancellationToken);

        return Ok(new WishlistResponse(state, user.WishlistSyncedAt, items, user.MinViableDiscountPercent));
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences(
        [FromBody] WishlistPreferencesRequest request,
        CancellationToken cancellationToken)
    {
        var percent = request?.MinViableDiscountPercent
            ?? throw new ArgumentException("MinViableDiscountPercent is required", nameof(request));
        if (percent < 0 || percent > 95)
        {
            throw new ArgumentException("MinViableDiscountPercent must be between 0 and 95", nameof(request));
        }

        var userId = GetUserId();
        var found = await _repository.ExecuteInTransactionAsync(async () =>
        {
            var user = await _repository.GetTrack<User>()
                .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
            if (user is null)
            {
                return false;
            }

            user.MinViableDiscountPercent = percent;
            await _repository.SaveChangesAsync();
            return true;
        });

        if (!found)
        {
            return NotFound();
        }

        return Ok(new WishlistPreferencesResponse(percent));
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        // List snapshot only. The per-game price refresh takes tens of minutes and lives in the
        // background job; letting it run here would hang the request.
        // Recorded as a manual run so job_runs stays a truthful history, but excluded from the background
        // job's gate: it does not do the expensive price pass, so it must not delay one.
        var jobRunId = await _jobRunLog.StartAsync(JobRunLog.WishlistSync, JobRunLog.ManualTrigger, cancellationToken);

        WishlistListSyncReport report;
        try
        {
            report = await _wishlistSyncService.SyncListAsync(cancellationToken);
        }
        catch
        {
            await _jobRunLog.FinishAsync(jobRunId, JobRunStatuses.Failed, null, CancellationToken.None);
            throw;
        }

        await _jobRunLog.FinishAsync(jobRunId, JobRunStatuses.Ok, new
        {
            report.State,
            report.ItemCount,
            report.Added,
            report.Updated,
            report.Removed,
            report.FetchedFromSteam,
            report.FetchFailed
        }, CancellationToken.None);

        return Ok(new WishlistSyncResponse(
            report.State,
            report.ItemCount,
            report.Added,
            report.Updated,
            report.Removed,
            Refreshed: 0,
            Failed: 0,
            report.SyncedAt,
            report.FetchedFromSteam,
            report.FetchFailed));
    }

    private async Task<IReadOnlyList<WishlistItemResponse>> LoadItemsAsync(int userId, CancellationToken cancellationToken)
    {
        var rows = await _repository.Get<UserLibrary>()
            .Where(entry => entry.UserId == userId && entry.Store == SteamStore && entry.State == WishedState)
            // PostgreSQL sorts NULLs last on ASC, so entries with no priority land after the ranked ones.
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.Title)
            .ToListAsync(cancellationToken);

        var appIds = rows
            .Select(entry => ParseAppId(entry.StoreGameId))
            .Where(appId => appId > 0)
            .Distinct()
            .ToList();

        var games = await _repository.Get<SteamGame>()
            .Where(game => game.Region == Region && appIds.Contains(game.AppId))
            .ToListAsync(cancellationToken);
        var gamesByAppId = games.ToDictionary(game => game.AppId);

        // One aggregate query for the whole list, never one per row: PostgreSQL computes the three minima
        // and only the grouped result is materialized.
        //  - HistoryLowMinor: lowest game_offers.history_low_all_minor recorded in MXN. The history low is
        //    stored raw with its own currency, so mixing currencies would compare pesos with dollars.
        //  - BestOfficialMinor: cheapest current MXN price among non-keyshop offers. "official" (ITAD
        //    allowlist) and "authorized" (gg.deals retail and other legitimate shops) are NOT split: the
        //    distinction that matters is legitimate shop vs keyshop, and gg.deals never emits "official".
        //  - BestKeyshopMinor: cheapest current MXN price among keyshop offers.
        // Rows with pricing_type "unconverted" are excluded: an unconverted amount is not a comparable MXN
        // price. Nulls are ignored by the minima and 0 stays a real price (free games exist).
        var steamGameIds = games.Select(game => game.SteamGameId).Distinct().ToList();
        var aggregatesByGameId = await _repository.Get<GameOffer>()
            // A row with no Steam snapshot (source='microsoft') is not part of this aggregate, which is keyed
            // by the Steam snapshot the wishlist rows carry.
            .Where(offer => offer.SteamGameId != null && steamGameIds.Contains(offer.SteamGameId.Value))
            .GroupBy(offer => offer.SteamGameId)
            .Select(group => new
            {
                SteamGameId = group.Key,
                HistoryLowMinor = group.Min(offer =>
                    offer.HistoryLowCurrency == MxnCurrency ? offer.HistoryLowAllMinor : null),
                BestOfficialMinor = group.Min(offer =>
                    offer.Classification != KeyshopClassification &&
                    offer.PricingType != UnconvertedPricing
                        ? offer.MxnCurrentPriceMinor
                        : null),
                BestKeyshopMinor = group.Min(offer =>
                    offer.Classification == KeyshopClassification &&
                    offer.PricingType != UnconvertedPricing
                        ? offer.MxnCurrentPriceMinor
                        : null)
            })
            .ToDictionaryAsync(aggregate => aggregate.SteamGameId!.Value, cancellationToken);

        var items = new List<WishlistItemResponse>(rows.Count);
        foreach (var row in rows)
        {
            var appId = ParseAppId(row.StoreGameId);
            if (appId <= 0)
            {
                // StoreGameId is written from a validated appid, so a non-numeric row cannot be rendered.
                continue;
            }

            gamesByAppId.TryGetValue(appId, out var game);

            int? historyLowMinor = null;
            int? bestOfficialMinor = null;
            int? bestKeyshopMinor = null;
            if (game is not null && aggregatesByGameId.TryGetValue(game.SteamGameId, out var aggregate))
            {
                historyLowMinor = aggregate.HistoryLowMinor;
                bestOfficialMinor = aggregate.BestOfficialMinor;
                bestKeyshopMinor = aggregate.BestKeyshopMinor;
            }

            // steam_games.name is the Steam detail snapshot; user_library.title is the list snapshot and
            // may still be the appid placeholder before the first price refresh.
            var name = !string.IsNullOrWhiteSpace(game?.Name) ? game!.Name : row.Title;

            items.Add(new WishlistItemResponse(
                appId,
                name,
                game?.ImageUrl,
                row.Priority,
                row.AddedAt,
                game?.ItadGameId ?? row.ItadGameId,
                LatestRefresh(game?.OffersRefreshedAt, game?.GgDealsRefreshedAt),
                // Undiscounted Steam list price, straight from the persisted snapshot.
                game?.InitialPriceMinor,
                game?.Currency,
                historyLowMinor,
                historyLowMinor is null ? null : MxnCurrency,
                bestOfficialMinor,
                bestKeyshopMinor));
        }

        return items;
    }

    private static int ParseAppId(string storeGameId) =>
        int.TryParse(storeGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : 0;

    private static DateTime? LatestRefresh(DateTime? first, DateTime? second) =>
        first is null ? second
        : second is null ? first
        : first > second ? first
        : second;

    private int GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(value, out var userId) && userId > 0
            ? userId
            : throw new UnauthorizedAccessException("Missing or invalid user identity claim");
    }
}
