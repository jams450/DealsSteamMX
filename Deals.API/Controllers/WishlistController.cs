using System.Globalization;
using System.Security.Claims;
using Deals.API.Models.Wishlist;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

    // Ceiling of the package preview. Acota el payload y el trabajo por petición; el cliente aplica el
    // mismo número para no construir una petición que va a fallar.
    private const int MaxPackageAppIds = 200;

    // ~1,000 millones de pesos en unidad mínima. Una oferta por encima de esto es dato corrupto, no un
    // precio: se excluye del subtotal y se cuenta como faltante. Con este tope, 200 sumandos quedan muy por
    // debajo de long.MaxValue, así que la suma no puede desbordar.
    private const long MaxPackagePriceMinor = 99_999_999_900L;

    private readonly IRepository _repository;
    private readonly IWishlistSyncService _wishlistSyncService;
    private readonly JobRunLog _jobRunLog;
    private readonly ILogger<WishlistController> _logger;
    private readonly AppDbContext _db;

    public WishlistController(
        IRepository repository,
        IWishlistSyncService wishlistSyncService,
        JobRunLog jobRunLog,
        ILogger<WishlistController> logger,
        AppDbContext db)
    {
        _repository = repository;
        _wishlistSyncService = wishlistSyncService;
        _jobRunLog = jobRunLog;
        _logger = logger;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] WishlistQueryRequest request, CancellationToken cancellationToken = default)
    {
        ValidateQuery(request);
        var userId = GetUserId();
        var user = await _repository.Get<User>().FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (user is null) return NotFound();

        var categoryIds = request.CategoryIds?.Distinct().ToList() ?? [];
        var rows =
            from row in _repository.Get<UserLibrary>()
            where row.UserId == userId && row.Store == SteamStore && row.State == WishedState
            join snapshot in _repository.Get<SteamGame>().Where(game => game.Region == Region)
                on row.StoreGameId equals snapshot.AppId.ToString() into snapshots
            from game in snapshots.DefaultIfEmpty()
            let name = game != null && game.Name != "" ? game.Name : row.Title
            let officialOffers = _repository.Get<GameOffer>().Where(offer => game != null && offer.SteamGameId == game.SteamGameId && offer.Classification != KeyshopClassification && offer.PricingType != UnconvertedPricing).ToList()
            let keyshopOffers = _repository.Get<GameOffer>().Where(offer => game != null && offer.SteamGameId == game.SteamGameId && offer.Classification == KeyshopClassification && offer.PricingType != UnconvertedPricing).ToList()
            let offeredOfficial = officialOffers.Min(offer => offer.MxnCurrentPriceMinor)
            let steamOfficial = game != null && game.Currency == MxnCurrency ? game.CurrentPriceMinor : null
            let official = offeredOfficial == null ? steamOfficial : steamOfficial == null ? offeredOfficial : offeredOfficial < steamOfficial ? offeredOfficial : steamOfficial
            let keyshop = keyshopOffers.Min(offer => offer.MxnCurrentPriceMinor)
            let bestPrice = official == null ? keyshop : keyshop == null ? official : official < keyshop ? official : keyshop
            // These percentages use the same MXN Steam list-price baseline as the two table columns.
            // A free/unknown/non-MXN base cannot produce a meaningful discount, so it remains null.
            let basePrice = game != null && game.Currency == MxnCurrency && game.InitialPriceMinor > 0 ? game.InitialPriceMinor : null
            let officialDiscount = basePrice == null || official == null ? (decimal?)null : 100m * (basePrice.Value - official.Value) / basePrice.Value
            let keyshopDiscount = basePrice == null || keyshop == null ? (decimal?)null : 100m * (basePrice.Value - keyshop.Value) / basePrice.Value
            let bestDiscount = new[]
            {
                game != null ? game.DiscountPercent : null,
                officialOffers.Max(offer => offer.DiscountPercent),
                keyshopOffers.Max(offer => offer.DiscountPercent)
            }.Max()
            select new { row, name, official, keyshop, bestPrice, bestDiscount, officialDiscount, keyshopDiscount };

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $"%{EscapeLikePattern(request.Search.Trim())}%";
            rows = rows.Where(entry => EF.Functions.ILike(entry.name, pattern, "\\") || EF.Functions.ILike(entry.row.StoreGameId, pattern, "\\"));
        }
        if (request.MinPrice is not null || request.MaxPrice is not null)
        {
            rows = rows.Where(entry =>
                (entry.official != null && (request.MinPrice == null || entry.official >= request.MinPrice) && (request.MaxPrice == null || entry.official <= request.MaxPrice)) ||
                (entry.keyshop != null && (request.MinPrice == null || entry.keyshop >= request.MinPrice) && (request.MaxPrice == null || entry.keyshop <= request.MaxPrice)));
        }
        if (request.Owned is not null)
        {
            var owned = request.Owned == "yes";
            rows = rows.Where(entry => (_repository.Get<UserLibrary>().Any(candidate => candidate.UserId == userId && candidate.State == Deals.BusinessLogic.Models.Library.LibraryStates.Owned && candidate.GameId != null && candidate.GameId == entry.row.GameId)) == owned);
        }
        if (request.Subscription is not null)
        {
            var subscription = request.Subscription == "yes";
            rows = rows.Where(entry => (_repository.Get<UserLibrary>().Any(candidate => candidate.UserId == userId && candidate.State == "subscription" && candidate.GameId != null && candidate.GameId == entry.row.GameId)) == subscription);
        }
        if (categoryIds.Count > 0 || request.Uncategorized)
        {
            rows = rows.Where(entry =>
                (categoryIds.Count > 0 && _repository.Get<WishlistCategoryItem>().Any(item => item.UserId == userId && item.UserLibraryId == entry.row.UserLibraryId && categoryIds.Contains(item.WishlistCategoryId))) ||
                (request.Uncategorized && !_repository.Get<WishlistCategoryItem>().Any(item => item.UserId == userId && item.UserLibraryId == entry.row.UserLibraryId)));
        }

        var totalItems = await rows.CountAsync(cancellationToken);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)request.PageSize);
        var page = totalPages == 0 ? 1 : Math.Min(request.Page, totalPages);
        var descending = request.Direction == "desc";
        var ordered = request.Sort switch
        {
            "name" => descending ? rows.OrderByDescending(entry => entry.name).ThenByDescending(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            "bestPrice" => descending ? rows.OrderBy(entry => entry.bestPrice == null).ThenByDescending(entry => entry.bestPrice).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.bestPrice == null).ThenBy(entry => entry.bestPrice).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            "bestDiscount" => descending ? rows.OrderBy(entry => entry.bestDiscount == null).ThenByDescending(entry => entry.bestDiscount).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.bestDiscount == null).ThenBy(entry => entry.bestDiscount).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            "officialDiscount" => descending ? rows.OrderBy(entry => entry.officialDiscount == null).ThenByDescending(entry => entry.officialDiscount).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.officialDiscount == null).ThenBy(entry => entry.officialDiscount).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            "keyshopDiscount" => descending ? rows.OrderBy(entry => entry.keyshopDiscount == null).ThenByDescending(entry => entry.keyshopDiscount).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.keyshopDiscount == null).ThenBy(entry => entry.keyshopDiscount).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            "officialPrice" => descending ? rows.OrderBy(entry => entry.official == null).ThenByDescending(entry => entry.official).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.official == null).ThenBy(entry => entry.official).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            "keyshopPrice" => descending ? rows.OrderBy(entry => entry.keyshop == null).ThenByDescending(entry => entry.keyshop).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.keyshop == null).ThenBy(entry => entry.keyshop).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId),
            _ => descending ? rows.OrderBy(entry => entry.row.Priority == null).ThenByDescending(entry => entry.row.Priority).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId) : rows.OrderBy(entry => entry.row.Priority == null).ThenBy(entry => entry.row.Priority).ThenBy(entry => entry.name).ThenBy(entry => entry.row.StoreGameId)
        };
        var appIds = await ordered.Skip((page - 1) * request.PageSize).Take(request.PageSize).Select(entry => entry.row.StoreGameId).ToListAsync(cancellationToken);
        var items = await LoadItemsAsync(userId, appIds.Select(ParseAppId).Where(appId => appId > 0).ToList(), cancellationToken);
        var itemsByAppId = items.ToDictionary(item => item.AppId);
        var orderedItems = appIds.Select(ParseAppId).Where(itemsByAppId.ContainsKey).Select(appId => itemsByAppId[appId]).ToList();
        var categories = await LoadCategoriesAsync(userId, cancellationToken);
        return Ok(new WishlistResponse(WishlistStates.Compose(user.SteamId64, user.WishlistSyncedAt, user.WishlistState), user.WishlistSyncedAt, orderedItems, user.MinViableDiscountPercent, categories, page, request.PageSize, totalItems, totalPages));
    }

    private static void ValidateQuery(WishlistQueryRequest request)
    {
        if (request.Page < 1) throw new ArgumentException("page must be positive", nameof(request.Page));
        if (request.PageSize is < 1 or > 100) throw new ArgumentException("pageSize must be between 1 and 100", nameof(request.PageSize));
        if (request.Search?.Trim().Length > 100) throw new ArgumentException("search cannot exceed 100 characters", nameof(request.Search));
        if (request.MinPrice < 0 || request.MaxPrice < 0 || request.MinPrice > request.MaxPrice) throw new ArgumentException("price range is invalid");
        if (request.Owned is not null && request.Owned is not ("yes" or "no")) throw new ArgumentException("owned must be yes or no", nameof(request.Owned));
        if (request.Subscription is not null && request.Subscription is not ("yes" or "no")) throw new ArgumentException("subscription must be yes or no", nameof(request.Subscription));
        if (request.CategoryIds is { Count: > 50 } || request.CategoryIds?.Any(id => id <= 0) == true) throw new ArgumentException("categoryIds must contain at most 50 positive values", nameof(request.CategoryIds));
        if (request.Sort is not null && request.Sort is not ("priority" or "name" or "bestPrice" or "bestDiscount" or "officialDiscount" or "keyshopDiscount" or "officialPrice" or "keyshopPrice")) throw new ArgumentException("sort is invalid", nameof(request.Sort));
        if (request.Direction is not null && request.Direction is not ("asc" or "desc")) throw new ArgumentException("direction must be asc or desc", nameof(request.Direction));
    }

    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        return Ok(await LoadCategoriesAsync(userId, cancellationToken));
    }

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory([FromBody] WishlistCategoryRequest request, CancellationToken cancellationToken)
    {
        var name = NormalizeCategoryName(request?.Name);
        var userId = GetUserId();
        await using var categoryLock = new PostgresAdvisoryLock(_db, CategoryLockKey(userId));
        if (!await categoryLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_CATEGORY_BUSY" });
        if (await _repository.Get<WishlistCategory>().AnyAsync(c => c.UserId == userId && c.NormalizedName == name.ToUpperInvariant(), cancellationToken))
            return Conflict(new { code = "WISHLIST_CATEGORY_EXISTS" });
        var category = new WishlistCategory { UserId = userId, Name = request!.Name!.Trim(), NormalizedName = name.ToUpperInvariant() };
        _repository.GetTrack<WishlistCategory>().Add(category);
        await _repository.SaveChangesAsync(cancellationToken);
        return Ok(new WishlistCategorySummary(category.WishlistCategoryId, category.Name, 0));
    }

    [HttpPatch("categories/{categoryId:long}")]
    public async Task<IActionResult> RenameCategory(long categoryId, [FromBody] WishlistCategoryRenameRequest request, CancellationToken cancellationToken)
    {
        var name = NormalizeCategoryName(request?.Name);
        var userId = GetUserId();
        await using var categoryLock = new PostgresAdvisoryLock(_db, CategoryLockKey(userId));
        if (!await categoryLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_CATEGORY_BUSY" });
        var category = await _repository.GetTrack<WishlistCategory>().FirstOrDefaultAsync(c => c.UserId == userId && c.WishlistCategoryId == categoryId, cancellationToken);
        if (category is null) return NotFound();
        if (await _repository.Get<WishlistCategory>().AnyAsync(c => c.UserId == userId && c.WishlistCategoryId != categoryId && c.NormalizedName == name.ToUpperInvariant(), cancellationToken)) return Conflict();
        category.Name = request!.Name!.Trim(); category.NormalizedName = name.ToUpperInvariant();
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("categories/{categoryId:long}")]
    public async Task<IActionResult> DeleteCategory(long categoryId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        await using var categoryLock = new PostgresAdvisoryLock(_db, CategoryLockKey(userId));
        if (!await categoryLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_CATEGORY_BUSY" });
        var category = await _repository.GetTrack<WishlistCategory>().FirstOrDefaultAsync(c => c.UserId == userId && c.WishlistCategoryId == categoryId, cancellationToken);
        if (category is null) return NotFound();
        _repository.GetTrack<WishlistCategory>().Remove(category);
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("categories/{categoryId:long}/items")]
    public Task<IActionResult> AddCategoryItems(long categoryId, [FromBody] WishlistCategoryAssignmentRequest request, CancellationToken cancellationToken) => ChangeCategoryItems(categoryId, request, true, cancellationToken);

    [HttpDelete("categories/{categoryId:long}/items")]
    public Task<IActionResult> RemoveCategoryItems(long categoryId, [FromBody] WishlistCategoryAssignmentRequest request, CancellationToken cancellationToken) => ChangeCategoryItems(categoryId, request, false, cancellationToken);

    [HttpPut("categories/{categoryId:long}/items")]
    public async Task<IActionResult> ReplaceCategoryItems(long categoryId, [FromBody] WishlistCategoryAssignmentRequest request, CancellationToken cancellationToken)
    {
        var appIds = ValidateAppIds(request?.AppIds);
        var userId = GetUserId();
        await using var categoryLock = new PostgresAdvisoryLock(_db, CategoryLockKey(userId));
        if (!await categoryLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_CATEGORY_BUSY" });
        if (!await _repository.Get<WishlistCategory>().AnyAsync(c => c.UserId == userId && c.WishlistCategoryId == categoryId, cancellationToken)) return NotFound();
        var ids = appIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
        var rows = await _repository.Get<UserLibrary>().Where(r => r.UserId == userId && r.Store == SteamStore && r.State == WishedState && ids.Contains(r.StoreGameId)).ToListAsync(cancellationToken);
        var libraryIds = rows.Select(r => r.UserLibraryId).ToHashSet();
        var existing = await _repository.GetTrack<WishlistCategoryItem>().Where(i => i.UserId == userId && i.WishlistCategoryId == categoryId).ToListAsync(cancellationToken);
        var desired = existing.Where(i => libraryIds.Contains(i.UserLibraryId)).ToDictionary(i => i.UserLibraryId);
        var changed = 0;
        foreach (var row in existing.Where(i => !libraryIds.Contains(i.UserLibraryId))) { _repository.GetTrack<WishlistCategoryItem>().Remove(row); changed++; }
        foreach (var row in rows.Where(r => !desired.ContainsKey(r.UserLibraryId))) { _repository.GetTrack<WishlistCategoryItem>().Add(new WishlistCategoryItem { UserId = userId, WishlistCategoryId = categoryId, UserLibraryId = row.UserLibraryId }); changed++; }
        await _repository.SaveChangesAsync(cancellationToken);
        return Ok(new WishlistCategoryBatchResponse(appIds.Count, rows.Count, changed, appIds.Count - rows.Count));
    }

    [HttpPut("items/{appId:int}/categories")]
    public async Task<IActionResult> ReplaceItemCategories(int appId, [FromBody] WishlistItemCategoriesRequest request, CancellationToken cancellationToken)
    {
        if (appId <= 0) throw new ArgumentException("AppID must be positive", nameof(appId));
        var categoryIds = request?.CategoryIds?.Distinct().ToList() ?? throw new ArgumentException("CategoryIds is required", nameof(request));
        if (categoryIds.Count > MaxPackageAppIds || categoryIds.Any(id => id <= 0)) throw new ArgumentException("CategoryIds must contain at most 200 positive integers", nameof(request));
        var userId = GetUserId();
        // A user-wide lock is stronger than the item/category lock granularity and keeps replace-item and
        // batch-category mutations from observing each other's half-applied set.
        await using var itemLock = new PostgresAdvisoryLock(_db, CategoryLockKey(userId));
        if (!await itemLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_CATEGORY_BUSY" });
        var library = await _repository.Get<UserLibrary>().FirstOrDefaultAsync(r => r.UserId == userId && r.Store == SteamStore && r.State == WishedState && r.StoreGameId == appId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (library is null) return NotFound();
        var categories = await _repository.Get<WishlistCategory>().Where(c => c.UserId == userId && categoryIds.Contains(c.WishlistCategoryId)).ToListAsync(cancellationToken);
        if (categories.Count != categoryIds.Count) return NotFound();
        var existing = await _repository.GetTrack<WishlistCategoryItem>().Where(i => i.UserId == userId && i.UserLibraryId == library.UserLibraryId).ToListAsync(cancellationToken);
        var requestedCategoryIds = categoryIds.ToHashSet();
        var existingCategoryIds = existing.Select(item => item.WishlistCategoryId).ToHashSet();
        var toRemove = existing.Where(item => !requestedCategoryIds.Contains(item.WishlistCategoryId)).ToList();
        var toAdd = requestedCategoryIds
            .Where(categoryId => !existingCategoryIds.Contains(categoryId))
            .Select(categoryId => new WishlistCategoryItem { UserId = userId, WishlistCategoryId = categoryId, UserLibraryId = library.UserLibraryId })
            .ToList();
        _repository.GetTrack<WishlistCategoryItem>().RemoveRange(toRemove);
        _repository.GetTrack<WishlistCategoryItem>().AddRange(toAdd);
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<IActionResult> ChangeCategoryItems(long categoryId, WishlistCategoryAssignmentRequest request, bool add, CancellationToken cancellationToken)
    {
        var appIds = ValidateAppIds(request?.AppIds);
        var userId = GetUserId();
        await using var categoryLock = new PostgresAdvisoryLock(_db, CategoryLockKey(userId));
        if (!await categoryLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_CATEGORY_BUSY" });
        if (!await _repository.Get<WishlistCategory>().AnyAsync(c => c.UserId == userId && c.WishlistCategoryId == categoryId, cancellationToken)) return NotFound();
        var ids = appIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
        var rows = await _repository.GetTrack<UserLibrary>().Where(r => r.UserId == userId && r.Store == SteamStore && r.State == WishedState && ids.Contains(r.StoreGameId)).ToListAsync(cancellationToken);
        var libraryIds = rows.Select(r => r.UserLibraryId).ToHashSet();
        var existing = await _repository.GetTrack<WishlistCategoryItem>().Where(i => i.UserId == userId && i.WishlistCategoryId == categoryId && libraryIds.Contains(i.UserLibraryId)).ToListAsync(cancellationToken);
        var changed = 0;
        if (add) { foreach (var row in rows.Where(r => existing.All(i => i.UserLibraryId != r.UserLibraryId))) { _repository.GetTrack<WishlistCategoryItem>().Add(new WishlistCategoryItem { UserId = userId, WishlistCategoryId = categoryId, UserLibraryId = row.UserLibraryId }); changed++; } }
        else { _repository.GetTrack<WishlistCategoryItem>().RemoveRange(existing); changed = existing.Count; }
        await _repository.SaveChangesAsync(cancellationToken);
        return Ok(new WishlistCategoryBatchResponse(appIds.Count, rows.Count, changed, appIds.Count - rows.Count));
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
            await _repository.SaveChangesAsync(cancellationToken);
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
        // AdminWithId remains intentional: wishlist sync is an admin-only operational action in current product policy.
        // List snapshot only. The per-game price refresh takes tens of minutes and lives in the
        // background job; letting it run here would hang the request.
        // Recorded as a manual run so job_runs stays a truthful history, but excluded from the background
        // job's gate: it does not do the expensive price pass, so it must not delay one.
        await using var syncLock = new PostgresAdvisoryLock(_db, 7_831_442_091L);
        if (!await syncLock.TryAcquireAsync(cancellationToken)) return Conflict(new { code = "WISHLIST_SYNC_IN_PROGRESS" });

        var jobRunId = await TryStartRunAsync(cancellationToken);

        WishlistListSyncReport report;
        try
        {
            report = await _wishlistSyncService.SyncListAsync(cancellationToken);
        }
        catch
        {
            await TryFinishRunAsync(jobRunId, JobRunStatuses.Failed, null, CancellationToken.None);
            throw;
        }

        await TryFinishRunAsync(jobRunId, JobRunStatuses.Ok, new
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

    /// <summary>
    /// Cost of the selected games under two independent scenarios (official and keyshops). Read-only: it
    /// reuses the same loader the table uses, so a game priced here and the same game priced in its row can
    /// never disagree. Nothing is persisted and no bundle is applied.
    /// </summary>
    [EnableRateLimiting("wishlist-package-preview")]
    [HttpPost("package-preview")]
    public async Task<IActionResult> PackagePreview(
        [FromBody] WishlistPackagePreviewRequest request,
        CancellationToken cancellationToken)
    {
        var appIds = request?.AppIds ?? throw new ArgumentException("AppIds is required", nameof(request));
        if (appIds.Count == 0)
        {
            throw new ArgumentException("AppIds must contain at least one appid", nameof(request));
        }

        if (appIds.Count > MaxPackageAppIds)
        {
            throw new ArgumentException($"AppIds cannot contain more than {MaxPackageAppIds} entries", nameof(request));
        }

        // A non-positive appid is not a real Steam appid, so it is rejected instead of coming back as
        // "unmatched": the route only answers about games that can exist.
        if (appIds.Any(appId => appId <= 0))
        {
            throw new ArgumentException("Every appid must be a positive integer", nameof(request));
        }

        // Duplicates are deduplicated rather than rejected: the caller's intent is the same either way, and
        // counting a game twice would inflate the subtotal.
        var requestedAppIds = appIds.Distinct().ToList();
        var items = await LoadItemsAsync(GetUserId(), requestedAppIds, cancellationToken);
        var foundAppIds = items.Select(item => item.AppId).ToHashSet();

        var official = Summarize(items, item => item.BestOfficialMinor);
        var keyshop = Summarize(items, item => item.BestKeyshopMinor);

        return Ok(new WishlistPackagePreviewResponse(
            requestedAppIds.Count,
            items.Count,
            requestedAppIds.Where(appId => !foundAppIds.Contains(appId)).ToList(),
            official.Subtotal,
            official.Quoted,
            official.MissingAppIds.Count,
            official.MissingAppIds,
            keyshop.Subtotal,
            keyshop.Quoted,
            keyshop.MissingAppIds.Count,
            keyshop.MissingAppIds,
            MxnCurrency,
            DateTime.UtcNow));
    }

    /// <summary>
    /// Adds up one scenario of a package: the subtotal, how many games were quoted and which ones were not.
    /// A negative or absurdly large amount is corrupt data, not a price, so it is excluded and counted as
    /// missing: one bad row can neither shrink nor inflate what the user is about to spend. A scenario with
    /// no quoted game returns a null subtotal, never a zero.
    /// </summary>
    private static PackageSubtotal Summarize(
        IReadOnlyList<WishlistItemResponse> items,
        Func<WishlistItemResponse, int?> price)
    {
        long subtotal = 0;
        var quoted = 0;
        var missingAppIds = new List<int>();

        foreach (var item in items)
        {
            var amount = price(item);
            // La comparación se hace en long a propósito: el tope no cabe en int, y compararlo contra un int
            // sería siempre falso (y el compilador lo avisa).
            if (amount is null || amount.Value < 0 || (long)amount.Value > MaxPackagePriceMinor)
            {
                missingAppIds.Add(item.AppId);
                continue;
            }

            // Cannot overflow: at most MaxPackageAppIds summands, each one capped at MaxPackagePriceMinor.
            subtotal += amount.Value;
            quoted++;
        }

        return new PackageSubtotal(quoted == 0 ? null : subtotal, quoted, missingAppIds);
    }

    private sealed record PackageSubtotal(long? Subtotal, int Quoted, IReadOnlyList<int> MissingAppIds);

    private async Task<long> TryStartRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _jobRunLog.StartAsync(JobRunLog.WishlistSync, JobRunLog.ManualTrigger, cancellationToken);
        }
        catch (Exception exception)
        {
            // Fail open: a log write must never break the manual sync. 0 means "not recorded", so FinishAsync
            // then finds no row and returns.
            _logger.LogWarning(exception, "[wishlist.sync] could not record the manual run");
            return 0L;
        }
    }

    private async Task TryFinishRunAsync(long jobRunId, string status, object? details, CancellationToken cancellationToken)
    {
        try
        {
            await _jobRunLog.FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "[wishlist.sync] could not record the manual run outcome");
        }
    }

    private async Task<IReadOnlyList<WishlistItemResponse>> LoadItemsAsync(
        int userId,
        IReadOnlyCollection<int>? appIds,
        CancellationToken cancellationToken)
    {
        var rowsQuery = _repository.Get<UserLibrary>()
            .Where(entry => entry.UserId == userId && entry.Store == SteamStore && entry.State == WishedState);

        if (appIds is not null)
        {
            // Un preview del paquete acota la lectura a la selección en vez de traer la wishlist entera.
            // StoreGameId guarda el appid como texto invariante de solo dígitos (ver ParseAppId), así que el
            // ida y vuelta es exacto y el filtro baja a SQL.
            var storeGameIds = appIds
                .Select(appId => appId.ToString(CultureInfo.InvariantCulture))
                .ToList();
            rowsQuery = rowsQuery.Where(entry => storeGameIds.Contains(entry.StoreGameId));
        }

        var rows = await rowsQuery
            // PostgreSQL sorts NULLs last on ASC, so entries with no priority land after the ranked ones.
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.Title)
            .ToListAsync(cancellationToken);

        var steamAppIds = rows
            .Select(entry => ParseAppId(entry.StoreGameId))
            .Where(appId => appId > 0)
            .Distinct()
            .ToList();

        var games = await _repository.Get<SteamGame>()
            .Where(game => game.Region == Region && steamAppIds.Contains(game.AppId))
            .ToListAsync(cancellationToken);
        var gamesByAppId = games.ToDictionary(game => game.AppId);

        // One ownership query for the whole wishlist. Canonical game identity is authoritative; Steam is
        // excluded because every row here is already a Steam wishlist entry. Subscriptions are not owned.
        var canonicalGameIds = games.Select(game => (long?)game.GameId).ToList();
        var ownedStoresByGameId = await _repository.Get<UserLibrary>()
            .Where(entry => entry.UserId == userId &&
                entry.State == Deals.BusinessLogic.Models.Library.LibraryStates.Owned &&
                entry.Store != SteamStore &&
                entry.GameId != null && canonicalGameIds.Contains(entry.GameId))
            .GroupBy(entry => entry.GameId!.Value)
            .Select(group => new
            {
                GameId = group.Key,
                Stores = group.Select(entry => entry.Store).Distinct().ToList()
            })
            .ToDictionaryAsync(group => group.GameId, group => group.Stores, cancellationToken);

        // One aggregate query for the whole list, never one per row: PostgreSQL computes the three minima
        // and only the grouped result is materialized.
        //  - HistoryLowMinor: lowest game_offers.history_low_all_minor recorded in MXN. The history low is
        //    stored raw with its own currency, so mixing currencies would compare pesos with dollars.
        //  - BestOfficialMinor: cheapest current MXN price among non-keyshop offers. "official" (ITAD
        //    allowlist) and "authorized" (gg.deals retail and other legitimate shops) are NOT split: the
        //    distinction that matters is legitimate shop vs keyshop, and gg.deals never emits "official".
        //    The direct Steam snapshot joins this minimum outside the query: Steam is not a row here.
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

        var libraryIdsForRows = rows.Select(row => row.UserLibraryId).ToList();
        var categoryAssignments = await _repository.Get<WishlistCategoryItem>()
            .Where(item => item.UserId == userId && libraryIdsForRows.Contains(item.UserLibraryId))
            .Join(_repository.Get<WishlistCategory>(),
                item => item.WishlistCategoryId,
                category => category.WishlistCategoryId,
                (item, category) => new { item.UserLibraryId, Category = new WishlistCategorySummary(category.WishlistCategoryId, category.Name, 0) })
            .ToListAsync(cancellationToken);
        var categoriesByLibraryId = categoryAssignments
            .GroupBy(item => item.UserLibraryId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WishlistCategorySummary>)group.Select(item => item.Category).OrderBy(category => category.Name).ToList());

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

            // Steam's own price competes for the best official price and does not live in game_offers: it is
            // the snapshot column. `Min` over a sequence ignores nulls and returns null only when both are,
            // so a game with no direct Steam price keeps the aggregate's answer instead of losing it. The
            // currency guard is honest rather than defensive: a non-MXN snapshot is not comparable to the
            // MXN minima, and converting it here would duplicate what fx_estimate already labels.
            var steamOfficialMinor = string.Equals(game?.Currency, MxnCurrency, StringComparison.OrdinalIgnoreCase)
                ? game?.CurrentPriceMinor
                : null;
            bestOfficialMinor = new[] { bestOfficialMinor, steamOfficialMinor }.Min();

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
                // One stamp per provider, straight from the snapshot: no derivation, so a failed provider
                // keeps its old date instead of inheriting a neighbour's. Steam's is observed_at, the time
                // its snapshot was taken.
                game?.ObservedAt,
                game?.OffersRefreshedAt,
                game?.GgDealsRefreshedAt,
                game?.EpicRefreshedAt,
                game?.MicrosoftRefreshedAt,
                // Undiscounted Steam list price, straight from the persisted snapshot.
                game?.InitialPriceMinor,
                game?.Currency,
                historyLowMinor,
                historyLowMinor is null ? null : MxnCurrency,
                bestOfficialMinor,
                bestKeyshopMinor,
                game?.GameId is long canonicalGameId && ownedStoresByGameId.TryGetValue(canonicalGameId, out var ownedStores)
                    ? ownedStores.OrderBy(store => store, StringComparer.Ordinal).ToList()
                    : [],
                 categoriesByLibraryId.TryGetValue(row.UserLibraryId, out var rowCategories) ? rowCategories : []));
        }

        return items;
    }

    private async Task<IReadOnlyList<WishlistCategorySummary>> LoadCategoriesAsync(int userId, CancellationToken cancellationToken)
    {
        var counts = await _repository.Get<WishlistCategoryItem>()
            .Where(item => item.UserId == userId)
            .GroupBy(item => item.WishlistCategoryId)
            .Select(group => new { CategoryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.CategoryId, group => group.Count, cancellationToken);

        var categories = await _repository.Get<WishlistCategory>()
            .Where(category => category.UserId == userId)
            .Select(category => new
            {
                category.WishlistCategoryId,
                category.Name
            })
            .ToListAsync(cancellationToken);

        return categories
            .Select(category => new WishlistCategorySummary(
                category.WishlistCategoryId,
                category.Name,
                counts.TryGetValue(category.WishlistCategoryId, out var count) ? count : 0))
            .OrderBy(category => category.Name)
            .ToList();
    }

    private static long CategoryLockKey(int userId) => unchecked(0x574C00000000L | (uint)userId);

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static int ParseAppId(string storeGameId) =>
        int.TryParse(storeGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : 0;

    private static DateTime? LatestRefresh(DateTime? first, DateTime? second) =>
        first is null ? second
        : second is null ? first
        : first > second ? first
        : second;

    private static List<int> ValidateAppIds(IReadOnlyList<int>? values)
    {
        var appIds = values?.Distinct().ToList() ?? throw new ArgumentException("AppIds is required");
        if (appIds.Count == 0 || appIds.Count > MaxPackageAppIds || appIds.Any(id => id <= 0))
            throw new ArgumentException("AppIds must contain 1..200 positive integers");
        return appIds;
    }

    private static string NormalizeCategoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Category name is required");
        var name = value.Trim();
        if (name.Length > 80) throw new ArgumentException("Category name cannot exceed 80 characters");
        return name;
    }

    private int GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(value, out var userId) && userId > 0
            ? userId
            : throw new UnauthorizedAccessException("Missing or invalid user identity claim");
    }
}
