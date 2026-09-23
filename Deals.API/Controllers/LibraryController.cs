using System.Security.Claims;
using Deals.API.Models.Library;
using Deals.API.Models.Reviews;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminWithId")]
public class LibraryController : ControllerBase
{
    // Hard cap enforced before model binding (endpoint level, no global server config). A body that
    // exceeds it is rejected with 413 by Kestrel.
    private const int MaxBodyBytes = 10 * 1024 * 1024;
    private const int MaxEntries = 10_000;

    // Frozen contract: the only accepted (PluginId, Source) pairs and the canonical store/state they map
    // to. A pair that is not here is reported as unsupportedSource and skipped, never guessed.
    private static readonly Dictionary<Guid, SourceRule> SourceRules = new()
    {
        [Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab")] = new("Steam", StoreKeys.Steam, LibraryStates.Owned),
        [Guid.Parse("00000002-dbd1-46c6-b5d0-b1ba559d10e4")] = new("Epic", StoreKeys.Epic, LibraryStates.Owned),
        [Guid.Parse("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e")] = new("GOG", StoreKeys.Gog, LibraryStates.Owned),
        [Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287")] = new("Xbox", StoreKeys.Xbox, LibraryStates.Subscription),
        [Guid.Parse("402674cd-4af6-4886-b6ec-0e695bfa0688")] = new("Amazon", StoreKeys.Amazon, LibraryStates.Owned),
        [Guid.Parse("c2f038e5-8b92-4877-91f1-da9094155fc5")] = new("Ubisoft Connect", StoreKeys.Ubisoft, LibraryStates.Owned),
        [Guid.Parse("96e8c4bc-ec5c-4c8b-87e7-18ee5a690626")] = new("Humble", StoreKeys.Humble, LibraryStates.Owned),
        [Guid.Parse("e3c26a3d-d695-4cb7-a769-5ff7612c7edd")] = new("Battle.net", StoreKeys.Battlenet, LibraryStates.Owned)
    };

    private readonly IRepository _repository;
    private readonly IGameIdentityResolver _gameIdentityResolver;
    private readonly ILibraryPriceBindingService _libraryPriceBindingService;
    private readonly IReviewService _reviewService;
    private readonly IFavoriteService _favoriteService;
    private readonly ILibraryCoverService _libraryCoverService;
    private readonly ILibraryStorePriceService _libraryStorePriceService;
    private readonly IManualLibraryService _manualLibraryService;
    private readonly IManualSearchService _manualSearchService;
    private readonly IConsoleLibraryImportService _consoleLibraryImportService;

    public LibraryController(
        IRepository repository,
        IGameIdentityResolver gameIdentityResolver,
        ILibraryPriceBindingService libraryPriceBindingService,
        IReviewService reviewService,
        IFavoriteService favoriteService,
        ILibraryCoverService libraryCoverService,
        ILibraryStorePriceService libraryStorePriceService,
        IManualLibraryService manualLibraryService,
        IManualSearchService manualSearchService,
        IConsoleLibraryImportService consoleLibraryImportService)
    {
        _repository = repository;
        _gameIdentityResolver = gameIdentityResolver;
        _libraryPriceBindingService = libraryPriceBindingService;
        _reviewService = reviewService;
        _favoriteService = favoriteService;
        _libraryCoverService = libraryCoverService;
        _libraryStorePriceService = libraryStorePriceService;
        _manualLibraryService = manualLibraryService;
        _manualSearchService = manualSearchService;
        _consoleLibraryImportService = consoleLibraryImportService;
    }

    [HttpPost("import")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Import(
        [FromBody] List<PlayniteLibraryEntry>? entries,
        CancellationToken cancellationToken)
    {
        if (entries is null || entries.Count == 0)
        {
            throw new ArgumentException("The library payload must be a non-empty JSON array", nameof(entries));
        }

        if (entries.Count > MaxEntries)
        {
            throw new ArgumentException($"The library payload must not exceed {MaxEntries} entries", nameof(entries));
        }

        var accepted = new List<AcceptedEntry>(entries.Count);
        var byStore = new Dictionary<string, int>(StringComparer.Ordinal);
        var unsupported = 0;

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("The library payload must not contain null entries", nameof(entries));
            }

            var gameId = RequireText(entry.GameId, nameof(PlayniteLibraryEntry.GameId));
            var pluginIdRaw = RequireText(entry.PluginId, nameof(PlayniteLibraryEntry.PluginId));
            var source = RequireText(entry.Source, nameof(PlayniteLibraryEntry.Source));
            var name = RequireText(entry.Name, nameof(PlayniteLibraryEntry.Name));
            var isInstalled = entry.IsInstalled
                ?? throw new ArgumentException("IsInstalled is required", nameof(PlayniteLibraryEntry.IsInstalled));
            var addedAt = ParseAdded(entry.Added);

            if (!Guid.TryParse(pluginIdRaw, out var pluginId))
            {
                throw new ArgumentException($"{nameof(PlayniteLibraryEntry.PluginId)} must be a GUID", nameof(PlayniteLibraryEntry.PluginId));
            }

            if (!SourceRules.TryGetValue(pluginId, out var rule) ||
                !string.Equals(rule.Source, source, StringComparison.Ordinal))
            {
                unsupported++;
                continue;
            }

            accepted.Add(new AcceptedEntry(rule.Store, gameId, name, rule.State, isInstalled, addedAt));
            byStore[rule.Store] = byStore.GetValueOrDefault(rule.Store) + 1;
        }

        var userId = GetUserId();
        var result = await _repository.ExecuteInTransactionAsync(async () =>
        {
            // Serializa la creación/reasignación de identidad canónica contra la fusión manual de juegos:
            // el resolver inserta game_external_ids y borra filas games huérfanas, así que una fusión
            // concurrente podría dejar un game_id colgando. ponytail: lock global; si algún día hay imports
            // concurrentes multi-usuario, fragmentar por usuario (la creación de identidad seguiría necesitando el global).
            await _repository.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))");

            var userExists = await _repository.Get<User>()
                .AnyAsync(candidate => candidate.UserId == userId, cancellationToken);
            if (!userExists)
            {
                return (Found: false, Imported: 0, Updated: 0);
            }

            // Tracked: every matching row is upserted in place. No prune: entries missing from the import
            // stay, because reviews and other user content must never be destroyed by a reimport.
            var existing = await _repository.GetTrack<UserLibrary>()
                .Where(row => row.UserId == userId)
                .ToListAsync(cancellationToken);
            var byKey = existing.ToDictionary(
                row => (row.Store, row.StoreGameId, row.State));

            var imported = 0;
            var updated = 0;
            var now = DateTime.UtcNow;
            var seen = new HashSet<(string Store, string StoreGameId, string State)>();

            foreach (var item in accepted)
            {
                var key = (item.Store, item.StoreGameId, item.State);
                // Duplicated entries inside one payload keep the first one.
                if (!seen.Add(key))
                {
                    continue;
                }

                // Canonical identity from the exact (store, store_game_id) pair only. No title matching:
                // the resolver either finds the mapping or creates a row for it.
                var game = await _gameIdentityResolver.ResolveOrCreateGameAsync(
                    new GameIdentityRequest(
                        item.Title,
                        [new GameExternalIdRef(item.Store, item.StoreGameId)]),
                    cancellationToken);

                if (byKey.TryGetValue(key, out var row))
                {
                    // ItadGameId and Priority are preserved on purpose: they belong to other passes.
                    row.Title = item.Title;
                    row.IsInstalled = item.IsInstalled;
                    row.AddedAt = item.AddedAt;
                    row.ImportedAt = now;
                    // A re-import (entries imported before the canonical catalog) still gets its game id.
                    row.GameId = game.GameId;
                    updated++;
                    continue;
                }

                _repository.GetTrack<UserLibrary>().Add(new UserLibrary
                {
                    UserId = userId,
                    Store = item.Store,
                    StoreGameId = item.StoreGameId,
                    Title = item.Title,
                    State = item.State,
                    IsInstalled = item.IsInstalled,
                    AddedAt = item.AddedAt,
                    ImportedAt = now,
                    GameId = game.GameId
                });
                imported++;
            }

            await _repository.SaveChangesAsync();
            return (Found: true, Imported: imported, Updated: updated);
        });

        if (!result.Found)
        {
            return NotFound();
        }

        return Ok(new LibraryImportResponse(result.Imported, result.Updated, 0, unsupported, byStore));
    }

    /// <summary>
    /// Fills missing covers from Steam for the games already identified by the catalog. One pass is bounded
    /// by <paramref name="request"/>'s limit and writes nothing but <c>games.image_url</c>: rows the pass
    /// cannot solve come back counted and are placed by hand from the grid.
    /// </summary>
    [HttpPost("covers/sync")]
    public async Task<IActionResult> SyncCovers(
        [FromBody] LibraryCoverSyncRequest? request,
        CancellationToken cancellationToken)
    {
        var limit = request?.Limit ?? LibraryCoverLimits.Default;
        if (limit < 1 || limit > LibraryCoverLimits.Max)
        {
            throw new ArgumentException(
                $"El límite debe estar entre 1 y {LibraryCoverLimits.Max} juegos",
                nameof(request));
        }

        var result = await _libraryCoverService.SyncMissingCoversAsync(GetUserId(), limit, cancellationToken);
        return Ok(LibraryCoverSyncResponse.From(result));
    }

    /// <summary>
    /// Prices the library rows Steam cannot price, today the Xbox ones, one bounded pass. This is the only
    /// writer of an offer without a Steam snapshot: the offer hangs off the canonical game, because the
    /// library row's store id (a PackageFamilyName) is not a Steam appid. A row already priced inside the
    /// refresh window is skipped, so calling this repeatedly walks the library instead of re-asking the store.
    /// </summary>
    [HttpPost("prices/sync")]
    public async Task<IActionResult> SyncStorePrices(
        [FromBody] LibraryStorePriceSyncRequest? request,
        CancellationToken cancellationToken)
    {
        var limit = request?.Limit ?? LibraryStorePriceLimits.Default;
        if (limit < 1 || limit > LibraryStorePriceLimits.Max)
        {
            throw new ArgumentException(
                $"El límite debe estar entre 1 y {LibraryStorePriceLimits.Max} juegos",
                nameof(request));
        }

        var result = await _libraryStorePriceService.SyncStorePricesAsync(
            GetUserId(),
            limit,
            cancellationToken);
        return Ok(LibraryStorePriceSyncResponse.From(result));
    }

    /// <summary>
    /// Provider title search for the manual-add dialog: covers and years to pick from (IGDB, measured in
    /// docs/PLAN_CONSOLE.md §2.3). Read-only; it never writes and never decides identity. A null
    /// <c>source</c> means the provider is unavailable, which the dialog shows as such — not as "no hits".
    /// Catalog candidates have no GET endpoint: they travel inside the POST /manual response, because
    /// only that call can be refused by the no-title-identity rule and only it needs them.
    /// </summary>
    [HttpGet("manual/enrich")]
    public async Task<IActionResult> GetManualEnrich(
        [FromQuery] string? title,
        CancellationToken cancellationToken)
    {
        var result = await _manualSearchService.SearchAsync(title ?? string.Empty, cancellationToken);
        return Ok(ManualSearchResponse.From(result));
    }

    /// <summary>
    /// Manual addition of a game (console or any platform) to the caller's library: the row hangs off the
    /// canonical game_id, written as store_game_id plus a platform mapping in game_external_ids
    /// (docs/PLAN_CONSOLE.md §4). Candidates are offered, never silently chosen. Answers 200 with an
    /// outcome (<c>created | attached | duplicate | candidates</c>), 404 when the user no longer exists.
    /// </summary>
    [HttpPost("manual")]
    public async Task<IActionResult> AddManual(
        [FromBody] ManualLibraryAddRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _manualLibraryService.AddAsync(
            GetUserId(),
            request.Store,
            request.Title,
            request.GameId,
            request.Create,
            request.IsInstalled,
            request.AddedAt,
            request.IgdbId,
            cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(ManualLibraryAddResponse.From(result));
    }

    /// <summary>
    /// Read-only preview of a console bulk import (<c>docs/PLAN_CONSOLE.md</c> §7): the Playnite export rows
    /// whose <c>Source</c> is null. Answers, per entry, the catalog candidates for its title — suggestions
    /// only, identity is never asserted by title — and the platform slugs the catalog can infer from the
    /// Playnite platform names, with <c>needsPlatform</c> when nothing can be suggested. It writes nothing.
    /// <c>POST /api/library/import</c> is untouched and still requires a non-null <c>Source</c>; this
    /// endpoint requires it null, so neither path accepts the other's payload.
    /// </summary>
    [HttpPost("console-import/preview")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> PreviewConsoleImport(
        [FromBody] List<ConsoleImportEntryRequest>? entries,
        CancellationToken cancellationToken)
    {
        var inputs = MapConsoleEntries(entries);
        var result = await _consoleLibraryImportService.PreviewAsync(GetUserId(), inputs, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(ConsoleImportPreviewResponse.From(result));
    }

    /// <summary>
    /// Commits explicit console import decisions. Each entry carries the platform the person chose (validated
    /// with <c>StoreKeys.Normalize</c>; the eight PC stores are refused) and exactly one identity action:
    /// <c>AttachGameId</c> for an existing canonical game or <c>Create</c> for a new one — a title never
    /// decides identity. The whole payload is validated before the first write, an identity conflict is 409
    /// with nothing written, and persistence is insert-only: an exact row already present is reported as
    /// <c>already_present</c>, never updated, deleted or pruned.
    /// </summary>
    [HttpPost("console-import/commit")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> CommitConsoleImport(
        [FromBody] List<ConsoleImportDecisionRequest>? decisions,
        CancellationToken cancellationToken)
    {
        var inputs = MapConsoleDecisions(decisions);
        var result = await _consoleLibraryImportService.CommitAsync(GetUserId(), inputs, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        var response = ConsoleImportCommitResponse.From(result);
        return result.Applied
            ? Ok(response)
            : StatusCode(StatusCodes.Status409Conflict, response);
    }

    /// <summary>
    /// Removes one library row of the caller. Undo for a manual addition — there was no way to delete any
    /// library row before. The canonical game, its mappings, reviews and favorites stay: identity and
    /// user content are shared, and a Playnite reimport can recreate the row.
    /// </summary>
    [HttpDelete("library/{userLibraryId:long}")]
    public async Task<IActionResult> DeleteLibraryRow(long userLibraryId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var row = await _repository.GetTrack<UserLibrary>()
            .FirstOrDefaultAsync(
                entry => entry.UserLibraryId == userLibraryId && entry.UserId == userId,
                cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        _repository.GetTrack<UserLibrary>().Remove(row);
        await _repository.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var userExists = await _repository.Get<User>()
            .AnyAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (!userExists)
        {
            return NotFound();
        }

        var rows = await _repository.Get<UserLibrary>()
            .Where(entry => entry.UserId == userId &&
                (entry.State == LibraryStates.Owned || entry.State == LibraryStates.Subscription))
            .OrderBy(entry => entry.Store)
            .ThenBy(entry => entry.Title)
            .ToListAsync(cancellationToken);

        // Read-only batch resolution: never creates games, never writes game_id.
        var bindings = await _libraryPriceBindingService.ResolveAsync(rows, cancellationToken);

        // One extra query for the whole page: the caller's reviews for every canonical game present.
        // No per-row lookup. Keyed by (gameId, platform) because a review lives on that pair, not on the
        // mutable user_library row, and a pair may hold several reviews (replays): the library row shows
        // the most recently written one, and the review drawer lists all of them.
        var gameIds = rows
            .Where(entry => entry.GameId is > 0)
            .Select(entry => entry.GameId!.Value)
            .Distinct()
            .ToList();
        // Cover art and the canonical title are display-only extras: one batch query for the same canonical
        // games. No writes. The canonical title wins over the imported row title so a Playnite reimport,
        // which keeps writing user_library.title, can never overwrite what the catalog says the game is.
        var gameById = gameIds.Count == 0
            ? new Dictionary<long, (string Title, string? ImageUrl)>()
            : (await _repository.Get<Game>()
                .Where(game => gameIds.Contains(game.GameId))
                .Select(game => new { game.GameId, game.Title, game.ImageUrl })
                .ToListAsync(cancellationToken))
                .ToDictionary(game => game.GameId, game => (game.Title, game.ImageUrl));

        // The same batch feeds both derived fields of a row: the representative review of the pair and every
        // year it was played (a replay on another year must show under both years in the report filter).
        var reviewsByGameAndPlatform = new Dictionary<(long GameId, string Platform), GameReview>();
        var yearsByGameAndPlatform = new Dictionary<(long GameId, string Platform), SortedSet<int>>();
        var reviews = await _reviewService.GetForGamesAsync(userId, gameIds, cancellationToken);
        foreach (var review in reviews)
        {
            var key = (review.GameId, review.Platform);
            if (!reviewsByGameAndPlatform.TryGetValue(key, out var current) || !IsAtLeastAsNew(current, review))
            {
                reviewsByGameAndPlatform[key] = review;
            }

            var year = PlayedYear(review);
            if (year is null)
            {
                continue;
            }

            if (yearsByGameAndPlatform.TryGetValue(key, out var years))
            {
                years.Add(year.Value);
            }
            else
            {
                yearsByGameAndPlatform[key] = new SortedSet<int> { year.Value };
            }
        }

        // Favorites are a separate mark, not a review: one batch query for the whole page.
        var favoriteGameIds = (await _favoriteService.GetFavoriteGameIdsAsync(userId, gameIds, cancellationToken))
            .ToHashSet();

        var items = rows
            .Select(entry =>
            {
                var binding = bindings.TryGetValue(entry.UserLibraryId, out var resolved)
                    ? resolved
                    : LibraryPriceBinding.None;
                GameReview? review = null;
                IReadOnlyList<int> playedYears = [];

                // The canonical title and the cover share the one batch lookup above. A row without a game
                // id (or without a canonical row) keeps the imported title: that is the fallback, not an error.
                var canonicalTitle = entry.Title;
                string? imageUrl = null;
                if (entry.GameId is > 0 && gameById.TryGetValue(entry.GameId.Value, out var gameDisplay))
                {
                    if (!string.IsNullOrWhiteSpace(gameDisplay.Title))
                    {
                        canonicalTitle = gameDisplay.Title;
                    }

                    imageUrl = gameDisplay.ImageUrl;
                }

                if (entry.GameId is > 0)
                {
                    var key = (entry.GameId.Value, entry.Store);
                    reviewsByGameAndPlatform.TryGetValue(key, out review);
                    playedYears = yearsByGameAndPlatform.TryGetValue(key, out var years)
                        ? years.Reverse().ToArray()
                        : [];
                }

                return new LibraryItemResponse(
                    entry.UserLibraryId,
                    entry.Store,
                    entry.StoreGameId,
                    canonicalTitle,
                    entry.State,
                    entry.IsInstalled,
                    entry.AddedAt,
                    entry.ImportedAt,
                    binding.PriceState,
                    binding.BindingSource,
                    binding.SteamAppId,
                    binding.BestOfficialMinor,
                    binding.BestKeyshopMinor,
                    binding.HistoryLowMinor,
                    binding.BasePriceMinor,
                    binding.BaseCurrency,
                    entry.GameId,
                    review is null ? null : ReviewResponse.From(review),
                    imageUrl,
                    entry.GameId is > 0 && favoriteGameIds.Contains(entry.GameId.Value),
                    playedYears);

            })
            .ToList();

        return Ok(items);
    }

    /// <summary>
    /// Year a review counts for, or null when it has no month. <c>finished_month</c> wins over
    /// <c>started_month</c>, and a run that crosses New Year is filed under the year it ended.
    /// </summary>
    private static int? PlayedYear(GameReview review)
    {
        var month = review.FinishedMonth ?? review.StartedMonth;
        return month?.Year;
    }

    /// <summary>True when <paramref name="current"/> was written at or after <paramref name="candidate"/>.</summary>
    private static bool IsAtLeastAsNew(GameReview current, GameReview candidate)
    {
        var currentStamp = current.Updated ?? current.Created ?? DateTime.MinValue;
        var candidateStamp = candidate.Updated ?? candidate.Created ?? DateTime.MinValue;
        return currentStamp > candidateStamp ||
            (currentStamp == candidateStamp && current.GameReviewId >= candidate.GameReviewId);
    }

    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{field} is required", field)
            : value;

    /// <summary>
    /// Maps the console preview payload to the business contract. A null element is a malformed payload, not
    /// an empty entry; the shape validation itself lives in the service.
    /// </summary>
    private static List<ConsoleImportEntryInput> MapConsoleEntries(List<ConsoleImportEntryRequest>? entries)
    {
        var inputs = new List<ConsoleImportEntryInput>(entries?.Count ?? 0);
        if (entries is null)
        {
            return inputs;
        }

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("El payload no puede contener entradas nulas", nameof(entries));
            }

            inputs.Add(new ConsoleImportEntryInput(
                entry.GameId,
                entry.Name,
                entry.Source,
                entry.Platforms,
                entry.IsInstalled,
                entry.Added));
        }

        return inputs;
    }

    /// <summary>Maps the console commit payload to the business contract, one decision per preview entry.</summary>
    private static List<ConsoleImportDecisionInput> MapConsoleDecisions(List<ConsoleImportDecisionRequest>? decisions)
    {
        var inputs = new List<ConsoleImportDecisionInput>(decisions?.Count ?? 0);
        if (decisions is null)
        {
            return inputs;
        }

        foreach (var decision in decisions)
        {
            if (decision is null)
            {
                throw new ArgumentException("El payload no puede contener entradas nulas", nameof(decisions));
            }

            inputs.Add(new ConsoleImportDecisionInput(
                decision.EntryId,
                decision.Name,
                decision.OwnedPlatform,
                decision.AttachGameId,
                decision.Create,
                decision.IsInstalled,
                decision.Added));
        }

        return inputs;
    }

    private static DateTime? ParseAdded(string? added)
    {
        var text = RequireText(added, nameof(PlayniteLibraryEntry.Added));
        return PlayniteDates.Parse(text, nameof(PlayniteLibraryEntry.Added));
    }

    private int GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(value, out var userId) && userId > 0
            ? userId
            : throw new UnauthorizedAccessException("Missing or invalid user identity claim");
    }

    private sealed record SourceRule(string Source, string Store, string State);

    private sealed record AcceptedEntry(
        string Store,
        string StoreGameId,
        string Title,
        string State,
        bool IsInstalled,
        DateTime? AddedAt);
}
