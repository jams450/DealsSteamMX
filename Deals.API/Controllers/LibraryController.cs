using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
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

    private static readonly Regex DotNetDatePattern = new(
        @"^/Date\((-?\d+)\)/$",
        RegexOptions.CultureInvariant);

    private readonly IRepository _repository;
    private readonly IGameIdentityResolver _gameIdentityResolver;
    private readonly ILibraryPriceBindingService _libraryPriceBindingService;
    private readonly IReviewService _reviewService;

    public LibraryController(
        IRepository repository,
        IGameIdentityResolver gameIdentityResolver,
        ILibraryPriceBindingService libraryPriceBindingService,
        IReviewService reviewService)
    {
        _repository = repository;
        _gameIdentityResolver = gameIdentityResolver;
        _libraryPriceBindingService = libraryPriceBindingService;
        _reviewService = reviewService;
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
        // Cover art is a display-only extra: one batch query for the same canonical games. No writes.
        var imageByGameId = gameIds.Count == 0
            ? new Dictionary<long, string?>()
            : await _repository.Get<Game>()
                .Where(game => gameIds.Contains(game.GameId))
                .Select(game => new { game.GameId, game.ImageUrl })
                .ToDictionaryAsync(game => game.GameId, game => game.ImageUrl, cancellationToken);

        var reviewsByGameAndPlatform = new Dictionary<(long GameId, string Platform), GameReview>();
        var reviews = await _reviewService.GetForGamesAsync(userId, gameIds, cancellationToken);
        foreach (var review in reviews)
        {
            var key = (review.GameId, review.Platform);
            if (reviewsByGameAndPlatform.TryGetValue(key, out var current) && IsAtLeastAsNew(current, review))
            {
                continue;
            }

            reviewsByGameAndPlatform[key] = review;
        }

        var items = rows
            .Select(entry =>
            {
                var binding = bindings.TryGetValue(entry.UserLibraryId, out var resolved)
                    ? resolved
                    : LibraryPriceBinding.None;
                GameReview? review = null;
                if (entry.GameId is > 0)
                {
                    reviewsByGameAndPlatform.TryGetValue((entry.GameId.Value, entry.Store), out review);
                }

                return new LibraryItemResponse(
                    entry.UserLibraryId,
                    entry.Store,
                    entry.StoreGameId,
                    entry.Title,
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
                    entry.GameId is > 0 && imageByGameId.TryGetValue(entry.GameId.Value, out var imageUrl)
                        ? imageUrl
                        : null);
            })
            .ToList();

        return Ok(items);
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

    private static DateTime? ParseAdded(string? added)
    {
        var text = RequireText(added, nameof(PlayniteLibraryEntry.Added));

        var match = DotNetDatePattern.Match(text);
        if (match.Success)
        {
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                throw new ArgumentException("Added is not a valid Playnite date", nameof(PlayniteLibraryEntry.Added));
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }

        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed.UtcDateTime;
        }

        throw new ArgumentException(
            "Added must be an ISO timestamp or /Date(<epoch-milliseconds>)/",
            nameof(PlayniteLibraryEntry.Added));
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
