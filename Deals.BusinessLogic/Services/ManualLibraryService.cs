using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Manual addition of a game to a user's library and the candidate lookup (<c>docs/PLAN_CONSOLE.md</c>
/// §4/§5). Identity: the id is always the canonical <c>games.game_id</c>, written twice — as
/// <c>user_library.store_game_id</c> and as the platform mapping <c>(namespace, external_id) =
/// (store, game_id)</c> in <c>game_external_ids</c>. A title match only produces candidates; the write
/// happens when a person chooses (<c>gameId</c>) or explicitly forces a new row (<c>create</c>), which is
/// what keeps <c>normalized_title</c> out of any identity decision (<c>PLAN_CATALOG.md</c> §4).
/// </summary>
public class ManualLibraryService : IManualLibraryService
{
    private const int MaxCandidates = 10;

    // games.title allows 512 but user_library.title only 256: the row is the binding constraint, so a
    // longer title is a 400 instead of a Postgres 22001 surfacing as a 500.
    private const int MaxTitleLength = 256;

    private readonly IRepository _repository;
    private readonly IManualSearchService _searchService;

    public ManualLibraryService(IRepository repository, IManualSearchService searchService)
    {
        _repository = repository;
        _searchService = searchService;
    }

    public async Task<IReadOnlyList<ManualGameCandidate>> CandidatesAsync(
        int userId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalized = GameTitleNormalizer.Normalize(title);
        if (normalized.Length == 0)
        {
            return [];
        }

        var games = await _repository.Get<Game>()
            .Where(game => game.NormalizedTitle == normalized)
            .OrderBy(game => game.Title)
            .ThenBy(game => game.GameId)
            .Take(MaxCandidates)
            .Select(game => new { game.GameId, game.Title, game.ReleaseYear, game.ImageUrl })
            .ToListAsync(cancellationToken);
        if (games.Count == 0)
        {
            return [];
        }

        // One extra query for the whole batch: which candidates already have a row in this library.
        var ids = games.Select(game => game.GameId).ToList();
        var inLibrary = (await _repository.Get<UserLibrary>()
                .Where(row => row.UserId == userId && row.GameId != null && ids.Contains(row.GameId.Value))
                .Select(row => row.GameId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return games
            .Select(game => new ManualGameCandidate(
                game.GameId, game.Title, game.ReleaseYear, game.ImageUrl, inLibrary.Contains(game.GameId)))
            .ToList();
    }

    public async Task<ManualLibraryAddResult?> AddAsync(
        int userId,
        string? store,
        string? title,
        long? gameId,
        bool create,
        bool? isInstalled,
        DateTime? addedAt,
        long? igdbId,
        CancellationToken cancellationToken = default)
    {
        var platform = StoreKeys.Normalize(store)
            ?? throw new ArgumentException(
                "La plataforma no es una tienda conocida ni un slug de plataforma válido", nameof(store));

        var cleanTitle = (title ?? string.Empty).Trim();
        if (cleanTitle.Length == 0)
        {
            throw new ArgumentException("El título es obligatorio", nameof(title));
        }

        if (cleanTitle.Length > MaxTitleLength)
        {
            throw new ArgumentException($"El título no puede exceder {MaxTitleLength} caracteres", nameof(title));
        }

        // Cover art is resolved before the transaction opens: no external HTTP while the global identity
        // lock is held. The URL never comes from the client — the server refetches the provider row by the
        // id the dialog picked — and the result is applied only to a game row created by this call.
        var artwork = gameId is null
            ? await _searchService.ArtworkAsync(igdbId ?? 0, cancellationToken)
            : null;

        return await _repository.ExecuteInTransactionAsync<ManualLibraryAddResult?>(async () =>
        {
            // Same global lock as the import and the manual merge: this path creates identity (a games
            // row plus its mapping), and the pair must serialize with the writers that reassign it.
            await _repository.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))");

            var userExists = await _repository.Get<User>()
                .AnyAsync(candidate => candidate.UserId == userId, cancellationToken);
            if (!userExists)
            {
                return null;
            }

            Game game;
            var created = 0;
            var attached = 0;

            if (gameId is { } chosen)
            {
                game = await _repository.Get<Game>()
                        .FirstOrDefaultAsync(candidate => candidate.GameId == chosen, cancellationToken)
                    ?? throw new ArgumentException("El juego elegido no existe en el catálogo", nameof(gameId));
                attached = 1;
            }
            else
            {
                var normalized = GameTitleNormalizer.Normalize(cleanTitle);
                var matches = await _repository.Get<Game>()
                    .CountAsync(candidate => candidate.NormalizedTitle == normalized, cancellationToken);
                if (matches > 0 && !create)
                {
                    // Refuse the write: identity is asserted by a person, never by the title alone.
                    return new ManualLibraryAddResult(
                        ManualLibraryOutcomes.Candidates, 0, 0, 0, 0, 0,
                        await CandidatesAsync(userId, cleanTitle, cancellationToken));
                }

                game = new Game { Title = cleanTitle, NormalizedTitle = normalized };
                if (artwork is not null)
                {
                    game.ImageUrl = artwork.Url;
                    game.ReleaseYear = artwork.ReleaseYear;
                }

                _repository.GetTrack<Game>().Add(game);
                await _repository.SaveChangesAsync();
                created = 1;
            }

            var storeGameId = game.GameId.ToString();

            // The platform relation is the point of the feature (PLAN_CONSOLE §4): an idempotent claim of
            // (store, game_id). `false` means the pair already belongs to another game after a merge —
            // not an error here: game_external_ids stays the merge truth and this row links game_id
            // directly, exactly like an imported row.
            await GameIdentityClaimer.TryClaimAsync(
                _repository, game.GameId, platform, storeGameId, cancellationToken);

            var duplicate = await _repository.Get<UserLibrary>().AnyAsync(
                row => row.UserId == userId && row.Store == platform && row.StoreGameId == storeGameId &&
                       row.State == LibraryStates.Owned,
                cancellationToken);
            if (duplicate)
            {
                return new ManualLibraryAddResult(
                    ManualLibraryOutcomes.Duplicate, created, attached, 1, game.GameId, 0, []);
            }

            var now = DateTime.UtcNow;
            var row = new UserLibrary
            {
                UserId = userId,
                Store = platform,
                StoreGameId = storeGameId,
                Title = cleanTitle,
                State = LibraryStates.Owned,
                IsInstalled = isInstalled,
                AddedAt = ToUtcKind(addedAt) ?? now,
                ImportedAt = now,
                GameId = game.GameId
            };
            _repository.GetTrack<UserLibrary>().Add(row);
            await _repository.SaveChangesAsync();

            return new ManualLibraryAddResult(
                created == 1 ? ManualLibraryOutcomes.Created : ManualLibraryOutcomes.Attached,
                created, attached, 0, game.GameId, row.UserLibraryId, []);
        });
    }

    // A JSON date-only binds as Kind=Unspecified, which Npgsql refuses to write to timestamptz.
    private static DateTime? ToUtcKind(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } => value,
        { Kind: DateTimeKind.Local } => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
    };
}
