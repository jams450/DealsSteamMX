using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Console bulk import from a Playnite export (<c>docs/PLAN_CONSOLE.md</c> §7), in two calls that mirror the
/// manual add of §4/§5 — one entry is just many:
///
/// <list type="number">
/// <item><description>
/// <see cref="PreviewAsync"/> is read-only: candidates by normalized title (never an identity claim) plus
/// the platform slugs the catalog can infer from the Playnite platform names. It writes nothing.
/// </description></item>
/// <item><description>
/// <see cref="CommitAsync"/> writes only explicit decisions: a platform the person chose and exactly one
/// identity action per entry — attach to a canonical game or create one. The whole payload is validated
/// before the first write, an identity conflict refuses the commit with no write at all, and an entry whose
/// exact library row already exists is reported <c>already_present</c> with zero writes (no <c>games</c> row,
/// no mapping, no <c>user_library</c> row) whatever its identity action is.
/// </description></item>
/// </list>
///
/// <para>
/// This is deliberately a parallel contract to <c>POST /api/library/import</c>: that one stays frozen and
/// keeps requiring a non-null <c>Source</c>, so a store export can never be imported as console by accident.
/// </para>
///
/// <para>
/// Reuse over copy: <see cref="GameTitleNormalizer"/> for the comparison key, the
/// <see cref="ManualGameCandidate"/> model for the candidate shape and <c>GameIdentityClaimer</c> for the
/// platform mapping — the same pieces the manual add uses. The candidate lookup is batched instead of
/// calling <c>IManualLibraryService.CandidatesAsync</c> per entry, because a payload of hundreds of entries
/// would otherwise be hundreds of round trips.
/// </para>
/// </summary>
public class ConsoleLibraryImportService : IConsoleLibraryImportService
{
    private readonly IRepository _repository;

    public ConsoleLibraryImportService(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<ConsoleImportPreviewResult?> PreviewAsync(
        int userId,
        IReadOnlyList<ConsoleImportEntryInput> entries,
        CancellationToken cancellationToken = default)
    {
        var parsed = ValidateEntries(entries);

        var userExists = await _repository.Get<User>()
            .AnyAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (!userExists)
        {
            return null;
        }

        var normalizedTitles = parsed
            .Select(entry => GameTitleNormalizer.Normalize(entry.Name))
            .Where(title => title.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var candidatesByTitle = await LoadCandidatesAsync(userId, normalizedTitles, cancellationToken);

        var items = parsed
            .Select((entry, index) =>
            {
                var normalized = GameTitleNormalizer.Normalize(entry.Name);
                IReadOnlyList<ManualGameCandidate> candidates =
                    normalized.Length > 0 && candidatesByTitle.TryGetValue(normalized, out var found)
                        ? found
                        : Array.Empty<ManualGameCandidate>();

                // Platforms travel as metadata: they only pre-select the dialog. No suggestion means the
                // entry asks for a platform instead of guessing one.
                var suggestions = ConsolePlatformCatalog.Suggest(entry.Platforms);

                return new ConsoleImportPreviewEntry(
                    index,
                    entry.EntryId,
                    entry.Name,
                    candidates,
                    suggestions,
                    suggestions.Count == 0);
            })
            .ToList();

        return new ConsoleImportPreviewResult(items);
    }

    public async Task<ConsoleImportCommitResult?> CommitAsync(
        int userId,
        IReadOnlyList<ConsoleImportDecisionInput> decisions,
        CancellationToken cancellationToken = default)
    {
        var plans = ValidateDecisions(decisions);

        return await _repository.ExecuteInTransactionAsync(async () =>
        {
            // Same global lock as the import, the manual add and the manual merge: this path creates and
            // claims canonical identity, so it must serialize with the writers that reassign it.
            await _repository.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))");

            var userExists = await _repository.Get<User>()
                .AnyAsync(candidate => candidate.UserId == userId, cancellationToken);
            if (!userExists)
            {
                return null;
            }

            await EnsureAttachTargetsExistAsync(plans, cancellationToken);

            // Refusal, not an error (same shape as a refused merge): detected before the first write, so the
            // commit moves nothing and the caller decides what to report.
            var conflicts = await FindConflictsAsync(plans, cancellationToken);
            if (conflicts.Count > 0)
            {
                return ConsoleImportCommitResult.Refused(conflicts);
            }

            // Insert-only lives here: every entry whose row already exists is resolved before the first
            // write, so an already_present outcome cannot leave a games row, a mapping or a library row
            // behind. Only the create decisions need this pre-flight — the attach ones know their
            // (platform, store_game_id) key up front and re-check it in the loop, still before any write.
            var alreadyPresentByEntry = await LoadAlreadyPresentCreateAsync(userId, plans, cancellationToken);

            var now = DateTime.UtcNow;
            var results = new List<ConsoleImportEntryResult>(plans.Count);
            var created = 0;
            var attached = 0;
            var alreadyPresent = 0;

            foreach (var plan in plans)
            {
                // Already applied (create decision resolved in the pre-flight above): reported as it is,
                // with zero writes for this entry.
                if (alreadyPresentByEntry.TryGetValue(plan.EntryId, out var alreadyPresentResult))
                {
                    alreadyPresent++;
                    results.Add(alreadyPresentResult);
                    continue;
                }

                long gameId;
                var createdGame = false;

                if (plan.AttachGameId is long attachGameId)
                {
                    // Verified before the lock and re-read here: the row must still exist.
                    gameId = attachGameId;

                    // The row is checked before the mapping is claimed: the claim is a write, and an entry
                    // whose row already exists must not write anything. This also resolves a payload that
                    // repeats the same (platform, game) decision in two entries — the second one finds the
                    // row the first one inserted, still with no claim of its own.
                    var presentRowId = await FindPresentRowAsync(
                        userId, plan.Platform, gameId, cancellationToken);
                    if (presentRowId is long existingRowId)
                    {
                        alreadyPresent++;
                        results.Add(new ConsoleImportEntryResult(
                            plan.EntryId,
                            ConsoleImportOutcomes.AlreadyPresent,
                            plan.Platform,
                            gameId,
                            existingRowId));
                        continue;
                    }
                }
                else
                {
                    // create Game solo explícito: the decision carries Create, never a title match.
                    var game = new Game
                    {
                        Title = plan.Name,
                        NormalizedTitle = GameTitleNormalizer.Normalize(plan.Name)
                    };
                    _repository.GetTrack<Game>().Add(game);
                    await _repository.SaveChangesAsync();
                    gameId = game.GameId;
                    createdGame = true;
                }

                var storeGameId = gameId.ToString(CultureInfo.InvariantCulture);

                // The platform relation is the point of the feature (PLAN_CONSOLE §4): (platform, game_id) is
                // claimed idempotently. The pre-flight owns the conflict decision, so a false here would mean
                // the identity state moved under the lock — abort rather than link a row to the wrong game.
                var claimed = await GameIdentityClaimer.TryClaimAsync(
                    _repository, gameId, plan.Platform, storeGameId, cancellationToken);
                if (!claimed)
                {
                    throw new InvalidOperationException(
                        $"El mapeo ({plan.Platform}, {storeGameId}) pertenece a otro juego canónico.");
                }

                // Only reachable from a create decision: its row is keyed by the id of the game inserted a
                // few lines above, so the pre-flight cannot have seen it, and the sequence never hands the
                // same id twice. A hit is an integrity failure, not an outcome — reporting already_present
                // past this point would mean an already_present entry wrote rows. The transaction rolls back.
                if (createdGame)
                {
                    var lateRowId = await FindPresentRowAsync(userId, plan.Platform, gameId, cancellationToken);
                    if (lateRowId is not null)
                    {
                        throw new InvalidOperationException(
                            $"La fila de biblioteca ({plan.Platform}, {storeGameId}) ya existía tras crear el juego canónico.");
                    }
                }

                var row = new UserLibrary
                {
                    UserId = userId,
                    Store = plan.Platform,
                    StoreGameId = storeGameId,
                    Title = plan.Name,
                    State = LibraryStates.Owned,
                    IsInstalled = plan.IsInstalled,
                    AddedAt = plan.AddedAt ?? now,
                    ImportedAt = now,
                    GameId = gameId
                };
                _repository.GetTrack<UserLibrary>().Add(row);
                await _repository.SaveChangesAsync();

                if (createdGame)
                {
                    created++;
                    results.Add(new ConsoleImportEntryResult(
                        plan.EntryId, ConsoleImportOutcomes.Created, plan.Platform, gameId, row.UserLibraryId));
                }
                else
                {
                    attached++;
                    results.Add(new ConsoleImportEntryResult(
                        plan.EntryId, ConsoleImportOutcomes.Attached, plan.Platform, gameId, row.UserLibraryId));
                }
            }

            return new ConsoleImportCommitResult(true, created, attached, alreadyPresent, results, []);
        });
    }

    /// <summary>
    /// Validates the whole preview payload before touching the database: entry id present and unique, title
    /// present and within the row width, <c>Source</c> null. Throws <see cref="ArgumentException"/> (400).
    /// </summary>
    private static List<ParsedEntry> ValidateEntries(IReadOnlyList<ConsoleImportEntryInput> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            throw new ArgumentException("El payload debe ser un arreglo no vacío", nameof(entries));
        }

        if (entries.Count > ConsoleImportLimits.MaxEntries)
        {
            throw new ArgumentException(
                $"El payload no puede exceder {ConsoleImportLimits.MaxEntries} entradas", nameof(entries));
        }

        var parsed = new List<ParsedEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var entryId = RequireText(entry.EntryId, "EntryId");
            if (!seen.Add(entryId))
            {
                throw new ArgumentException($"EntryId duplicado en el payload: {entryId}", "EntryId");
            }

            // The console contract reads the rows Playnite has no store plugin for. A store name here means
            // the store export was sent to the wrong endpoint: refuse instead of guessing. The PC import
            // keeps requiring a non-null Source, so neither path can swallow the other's payload.
            if (!string.IsNullOrWhiteSpace(entry.Source))
            {
                throw new ArgumentException(
                    "Source debe ser null explícito: las entradas de tienda van a POST /api/library/import",
                    "Source");
            }

            var platforms = (entry.Platforms ?? Array.Empty<string>())
                .Where(platform => !string.IsNullOrWhiteSpace(platform))
                .Select(platform => platform.Trim())
                .ToList();

            parsed.Add(new ParsedEntry(
                entryId,
                RequireTitle(entry.Name),
                platforms,
                entry.IsInstalled,
                PlayniteDates.Parse(entry.Added, "Added")));
        }

        return parsed;
    }

    /// <summary>
    /// Validates the whole commit payload before any write: entry id present and unique, title within the row
    /// width, platform a valid non-PC slug, and exactly one identity action per entry. Throws
    /// <see cref="ArgumentException"/> (400).
    /// </summary>
    private static List<CommitPlan> ValidateDecisions(IReadOnlyList<ConsoleImportDecisionInput> decisions)
    {
        if (decisions is null || decisions.Count == 0)
        {
            throw new ArgumentException("El payload debe ser un arreglo no vacío", nameof(decisions));
        }

        if (decisions.Count > ConsoleImportLimits.MaxEntries)
        {
            throw new ArgumentException(
                $"El payload no puede exceder {ConsoleImportLimits.MaxEntries} entradas", nameof(decisions));
        }

        var plans = new List<CommitPlan>(decisions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decision in decisions)
        {
            var entryId = RequireText(decision.EntryId, "EntryId");
            if (!seen.Add(entryId))
            {
                throw new ArgumentException($"EntryId duplicado en el payload: {entryId}", "EntryId");
            }

            var name = RequireTitle(decision.Name);

            var platform = StoreKeys.Normalize(decision.OwnedPlatform)
                ?? throw new ArgumentException(
                    "La plataforma no es una tienda conocida ni un slug de plataforma válido",
                    "OwnedPlatform");

            // The eight PC stores are the import's vocabulary: this endpoint only writes console/manual
            // platforms, so a store key here is a wrong payload, not a manual purchase note.
            if (StoreKeys.IsKnown(platform))
            {
                throw new ArgumentException(
                    "La importación de consola no acepta tiendas de PC: esas entradas van a POST /api/library/import",
                    "OwnedPlatform");
            }

            if (decision.Create == (decision.AttachGameId is not null))
            {
                throw new ArgumentException(
                    "Cada entrada necesita exactamente una acción de identidad: AttachGameId o Create",
                    "EntryId");
            }

            if (decision.AttachGameId is <= 0)
            {
                throw new ArgumentException("AttachGameId debe ser un id positivo", "AttachGameId");
            }

            plans.Add(new CommitPlan(
                entryId,
                name,
                platform,
                decision.AttachGameId,
                decision.IsInstalled,
                PlayniteDates.Parse(decision.Added, "Added")));
        }

        return plans;
    }

    /// <summary>
    /// One batch query for the whole payload, grouped and capped per title. Replicates the manual dialog's
    /// candidate semantics (same order, same <c>inLibrary</c> flag, same cap) without the N+1.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<ManualGameCandidate>>> LoadCandidatesAsync(
        int userId,
        List<string> normalizedTitles,
        CancellationToken cancellationToken)
    {
        var byTitle = new Dictionary<string, IReadOnlyList<ManualGameCandidate>>(StringComparer.Ordinal);
        if (normalizedTitles.Count == 0)
        {
            return byTitle;
        }

        var games = await _repository.Get<Game>()
            .Where(game => normalizedTitles.Contains(game.NormalizedTitle))
            .OrderBy(game => game.Title)
            .ThenBy(game => game.GameId)
            .Select(game => new { game.GameId, game.Title, game.ReleaseYear, game.ImageUrl, game.NormalizedTitle })
            .ToListAsync(cancellationToken);
        if (games.Count == 0)
        {
            return byTitle;
        }

        var gameIds = games.Select(game => game.GameId).Distinct().ToList();
        var inLibrary = (await _repository.Get<UserLibrary>()
                .Where(row => row.UserId == userId && row.GameId != null && gameIds.Contains(row.GameId.Value))
                .Select(row => row.GameId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var group in games.GroupBy(game => game.NormalizedTitle, StringComparer.Ordinal))
        {
            byTitle[group.Key] = group
                .Take(ConsoleImportLimits.MaxCandidatesPerEntry)
                .Select(game => new ManualGameCandidate(
                    game.GameId,
                    game.Title,
                    game.ReleaseYear,
                    game.ImageUrl,
                    inLibrary.Contains(game.GameId)))
                .ToList();
        }

        return byTitle;
    }

    /// <summary>Every named attach target must exist: a missing game is a bad decision (400), not a conflict.</summary>
    private async Task EnsureAttachTargetsExistAsync(List<CommitPlan> plans, CancellationToken cancellationToken)
    {
        var attachIds = plans
            .Where(plan => plan.AttachGameId is not null)
            .Select(plan => plan.AttachGameId!.Value)
            .Distinct()
            .ToList();
        if (attachIds.Count == 0)
        {
            return;
        }

        var found = await _repository.Get<Game>()
            .Where(game => attachIds.Contains(game.GameId))
            .Select(game => game.GameId)
            .ToListAsync(cancellationToken);

        var missing = attachIds.Except(found).OrderBy(id => id).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"El juego elegido no existe en el catálogo: {missing[0]}", "AttachGameId");
        }
    }

    /// <summary>
    /// Read-only pre-flight for the whole payload: the <c>(platform, canonical game id)</c> mapping each
    /// attach decision is about to claim may already belong to a different canonical game after a merge.
    /// One query for the batch; the exact pair is checked in memory.
    /// </summary>
    private async Task<List<ConsoleImportConflict>> FindConflictsAsync(
        List<CommitPlan> plans,
        CancellationToken cancellationToken)
    {
        var attachPlans = plans.Where(plan => plan.AttachGameId is not null).ToList();
        if (attachPlans.Count == 0)
        {
            return [];
        }

        var platforms = attachPlans.Select(plan => plan.Platform).Distinct().ToList();
        var externalIds = attachPlans
            .Select(plan => plan.AttachGameId!.Value.ToString(CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();

        var owners = await _repository.Get<GameExternalId>()
            .Where(row => platforms.Contains(row.NamespaceName) && externalIds.Contains(row.ExternalId))
            .Select(row => new { row.NamespaceName, row.ExternalId, row.GameId })
            .ToListAsync(cancellationToken);

        var ownerByPair = owners.ToDictionary(
            row => (row.NamespaceName, row.ExternalId),
            row => row.GameId);

        var conflicts = new List<ConsoleImportConflict>();
        foreach (var plan in attachPlans)
        {
            var gameId = plan.AttachGameId!.Value;
            var externalId = gameId.ToString(CultureInfo.InvariantCulture);
            if (ownerByPair.TryGetValue((plan.Platform, externalId), out var owner) && owner != gameId)
            {
                conflicts.Add(new ConsoleImportConflict(plan.EntryId, plan.Platform, gameId, owner));
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Exact existing row for <c>(user, platform, store_game_id = game id, owned)</c> — the unique key of
    /// <c>user_library</c>. Read-only, called before the mapping claim and before the row insert.
    /// </summary>
    private async Task<long?> FindPresentRowAsync(
        int userId,
        string platform,
        long gameId,
        CancellationToken cancellationToken)
    {
        var storeGameId = gameId.ToString(CultureInfo.InvariantCulture);

        return await _repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId &&
                          row.Store == platform &&
                          row.StoreGameId == storeGameId &&
                          row.State == LibraryStates.Owned)
            .Select(row => (long?)row.UserLibraryId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Read-only pre-flight of the entries that ask for a new canonical game. Their row is keyed by the id of
    /// the game the commit is about to insert, so it cannot be read before the write; the stable trace of a
    /// previous identical decision is the pair <c>(owned platform, canonical normalized_title)</c> — the same
    /// comparison key the preview uses for candidates, read from <c>games</c> and never from the payload
    /// title alone. A hit means the entry is already applied and is reported without a single write, which is
    /// what makes a re-run of the same payload a no-op instead of one new <c>games</c> row plus mapping plus
    /// library row per entry. Each existing row is consumed by at most one entry, so a payload that repeats
    /// the same title twice still gets one already_present per existing row and one create for the rest.
    /// </summary>
    private async Task<Dictionary<string, ConsoleImportEntryResult>> LoadAlreadyPresentCreateAsync(
        int userId,
        List<CommitPlan> plans,
        CancellationToken cancellationToken)
    {
        var present = new Dictionary<string, ConsoleImportEntryResult>(StringComparer.Ordinal);
        var createPlans = plans.Where(plan => plan.AttachGameId is null).ToList();
        if (createPlans.Count == 0)
        {
            return present;
        }

        // Normalize exactly like the write path stores it (Game.NormalizedTitle). A title that normalizes to
        // nothing cannot be matched, which is correct: its key is not a key.
        var keys = createPlans
            .Select(plan => (plan.Platform, Title: GameTitleNormalizer.Normalize(plan.Name)))
            .Where(key => key.Title.Length > 0)
            .Distinct()
            .ToList();
        if (keys.Count == 0)
        {
            return present;
        }

        var platforms = keys.Select(key => key.Platform).Distinct().ToList();
        var titles = keys.Select(key => key.Title).Distinct(StringComparer.Ordinal).ToList();

        var games = await _repository.Get<Game>()
            .Where(game => titles.Contains(game.NormalizedTitle))
            .Select(game => new { game.GameId, game.NormalizedTitle })
            .ToListAsync(cancellationToken);
        if (games.Count == 0)
        {
            return present;
        }

        var titleByGameId = games.ToDictionary(game => game.GameId, game => game.NormalizedTitle);
        var gameIds = games.Select(game => game.GameId).Distinct().ToList();

        var rows = await _repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId &&
                          row.State == LibraryStates.Owned &&
                          row.GameId != null &&
                          platforms.Contains(row.Store) &&
                          gameIds.Contains(row.GameId.Value))
            .OrderBy(row => row.GameId)
            .ThenBy(row => row.UserLibraryId)
            .Select(row => new { row.UserLibraryId, row.Store, row.GameId })
            .ToListAsync(cancellationToken);

        var rowsByKey = new Dictionary<(string Platform, string Title), List<(long RowId, long GameId)>>();
        foreach (var row in rows)
        {
            var key = (row.Store, titleByGameId[row.GameId!.Value]);
            if (!rowsByKey.TryGetValue(key, out var bucket))
            {
                bucket = [];
                rowsByKey[key] = bucket;
            }

            bucket.Add((row.UserLibraryId, row.GameId!.Value));
        }

        var consumed = new HashSet<long>();
        foreach (var plan in createPlans)
        {
            var title = GameTitleNormalizer.Normalize(plan.Name);
            if (title.Length == 0 || !rowsByKey.TryGetValue((plan.Platform, title), out var bucket))
            {
                continue;
            }

            foreach (var candidate in bucket)
            {
                if (!consumed.Add(candidate.RowId))
                {
                    continue;
                }

                present[plan.EntryId] = new ConsoleImportEntryResult(
                    plan.EntryId,
                    ConsoleImportOutcomes.AlreadyPresent,
                    plan.Platform,
                    candidate.GameId,
                    candidate.RowId);
                break;
            }
        }

        return present;
    }

    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{field} es obligatorio", field)
            : value.Trim();

    private static string RequireTitle(string? title)
    {
        var clean = (title ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("El título es obligatorio", "Name");
        }

        if (clean.Length > ConsoleImportLimits.MaxTitleLength)
        {
            throw new ArgumentException(
                $"El título no puede exceder {ConsoleImportLimits.MaxTitleLength} caracteres", "Name");
        }

        return clean;
    }

    private sealed record ParsedEntry(
        string EntryId,
        string Name,
        IReadOnlyList<string> Platforms,
        bool? IsInstalled,
        DateTime? AddedAt);

    private sealed record CommitPlan(
        string EntryId,
        string Name,
        string Platform,
        long? AttachGameId,
        bool? IsInstalled,
        DateTime? AddedAt);
}
