using System.Globalization;
using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Edits the canonical title of a game (<c>games.title</c> / <c>games.normalized_title</c>) and nothing
/// else: no merge, no library rows, no reviews, no covers and no release year. A Playnite reimport keeps
/// writing <c>user_library.title</c>; the library read-model projects this canonical value whenever a row
/// has a <c>game_id</c>, so the grid cannot be overwritten by a reimport.
///
/// <para>
/// Two modes, one write target. <c>manual</c> writes the trimmed title the admin typed. <c>igdb</c>
/// resolves the title from IGDB by id (the client never sends a title) and, in the same transaction,
/// claims the <c>('igdb', id)</c> pair. The provider call happens <em>before</em> the transaction opens:
/// no external HTTP while the global identity lock is held.
/// </para>
///
/// <para>
/// Conflicts never write: they come back as a refused result the controller turns into a 409, exactly like
/// the merge tool. Unavailable or empty lookups abort before the transaction with a mapped exception.
/// </para>
/// </summary>
public sealed class GameTitleEditService : IGameTitleEditService
{
    // Same global advisory lock as the import, the manual add and the manual merge: the igdb claim mutates
    // identity (game_external_ids), so it has to serialize with the writers that reassign it.
    private const string IdentityLockSql = "SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))";

    // Namespace vocabulary of game_external_ids: a provider namespace, like 'steam' for covers.
    private const string IgdbNamespace = "igdb";

    // games.title is VARCHAR(512); the normalized key cannot be longer than its source.
    private const int MaxTitleLength = 512;

    // Safe, wire-visible conflict reasons. They never carry provider payloads, URLs, ids or credentials.
    private const string IdentityTakenReason =
        "Ese juego de IGDB ya está vinculado a otro juego canónico.";
    private const string AlreadyLinkedReason =
        "Este juego ya tiene otro vínculo con IGDB; usa ese id o elimina el vínculo antes de cambiarlo.";

    private readonly IRepository _repository;
    private readonly IManualSearchService _manualSearchService;
    private readonly ILogger<GameTitleEditService> _logger;

    public GameTitleEditService(
        IRepository repository,
        IManualSearchService manualSearchService,
        ILogger<GameTitleEditService> logger)
    {
        _repository = repository;
        _manualSearchService = manualSearchService;
        _logger = logger;
    }

    public async Task<GameTitleEditResult> EditTitleAsync(
        long gameId,
        GameTitleEditCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (gameId <= 0)
        {
            throw new ArgumentException("El identificador del juego debe ser positivo.", nameof(gameId));
        }

        // The provider read is deliberately outside the transaction: an HTTP call must never run while the
        // global identity lock is held. An empty or unavailable lookup aborts here with nothing written.
        string? providerIgdbId = null;
        var title = command.Mode switch
        {
            GameTitleEditMode.Manual => RequireTitle(command.Title),
            GameTitleEditMode.Igdb => await ResolveIgdbTitleAsync(command, cancellationToken, id => providerIgdbId = id),
            _ => throw new ArgumentException("El modo de edición no es válido.", nameof(command))
        };

        var source = command.Mode == GameTitleEditMode.Igdb ? GameTitleSources.Igdb : GameTitleSources.Manual;

        return await _repository.ExecuteInTransactionAsync(async () =>
        {
            await _repository.ExecuteSqlRawAsync(IdentityLockSql);

            // Resolve the target under the lock: a concurrent merge cannot have absorbed it meanwhile.
            var game = await _repository.GetTrack<Game>()
                .FirstOrDefaultAsync(candidate => candidate.GameId == gameId, cancellationToken)
                ?? throw new GameNotFoundException(gameId);

            if (providerIgdbId is not null)
            {
                // The claim is checked before any write: a refused claim leaves the row untouched.
                var conflict = await ClaimIgdbAsync(gameId, providerIgdbId, cancellationToken);
                if (conflict is not null)
                {
                    return GameTitleEditResult.Conflict(gameId, conflict);
                }
            }

            game.Title = title;
            game.NormalizedTitle = GameTitleNormalizer.Normalize(title);
            await _repository.SaveChangesAsync();

            _logger.LogInformation(
                "Canonical title of game {GameId} edited from source {Source}", game.GameId, source);

            return GameTitleEditResult.Success(
                game.GameId,
                game.Title,
                game.NormalizedTitle,
                source,
                command.Mode == GameTitleEditMode.Igdb ? command.IgdbId : null);
        });
    }

    /// <summary>
    /// Reads the provider row by id and returns its title. The caller passes a sink for the normalized id so
    /// the transaction can claim it. Unavailable and not-found are distinct controlled failures.
    /// </summary>
    private async Task<string> ResolveIgdbTitleAsync(
        GameTitleEditCommand command,
        CancellationToken cancellationToken,
        Action<string> onResolved)
    {
        var igdbId = command.IgdbId
            ?? throw new ArgumentException("Se requiere el id de IGDB.", nameof(command));
        if (igdbId <= 0)
        {
            throw new ArgumentException("El id de IGDB debe ser mayor que cero.", nameof(command));
        }

        var lookup = await _manualSearchService.GameByIdAsync(igdbId, cancellationToken);
        if (lookup.Source is null)
        {
            throw new GameTitleSourceUnavailableException();
        }

        if (lookup.Game is null || string.IsNullOrWhiteSpace(lookup.Game.Title))
        {
            throw new GameTitleSourceNotFoundException(igdbId);
        }

        onResolved(igdbId.ToString(CultureInfo.InvariantCulture));
        return RequireTitle(lookup.Game.Title);
    }

    /// <summary>
    /// Claims the exact <c>('igdb', id)</c> pair for the target game. Returns a conflict reason (nothing
    /// written) when the id already belongs to another game or when the target carries a different IGDB
    /// link; returns null when the pair is now owned by this game, including the idempotent re-save.
    /// </summary>
    private async Task<string?> ClaimIgdbAsync(long gameId, string externalId, CancellationToken cancellationToken)
    {
        // The pair is the identity: if another canonical game owns it, refuse without writing.
        var owner = await _repository.Get<GameExternalId>()
            .Where(mapping => mapping.NamespaceName == IgdbNamespace && mapping.ExternalId == externalId)
            .Select(mapping => (long?)mapping.GameId)
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is not null && owner != gameId)
        {
            return IdentityTakenReason;
        }

        // A second IGDB link on the same game would make the mapping ambiguous, so the admin must clear the
        // existing one first instead of this call silently stacking another.
        var otherLink = await _repository.Get<GameExternalId>()
            .Where(mapping => mapping.GameId == gameId &&
                              mapping.NamespaceName == IgdbNamespace &&
                              mapping.ExternalId != externalId)
            .Select(mapping => mapping.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);
        if (otherLink is not null)
        {
            return AlreadyLinkedReason;
        }

        if (owner == gameId)
        {
            // Idempotent: the same pair is already claimed, only the title is (re)written.
            return null;
        }

        // ON CONFLICT DO NOTHING plus the re-read inside the claimer: a writer that raced the advisory lock
        // keeps its mapping and this call reports the conflict instead of overwriting it.
        var claimed = await GameIdentityClaimer.TryClaimAsync(
            _repository, gameId, IgdbNamespace, externalId, cancellationToken);
        return claimed ? null : IdentityTakenReason;
    }

    private static string RequireTitle(string? title)
    {
        var clean = (title ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("El título es obligatorio.", nameof(title));
        }

        if (clean.Length > MaxTitleLength)
        {
            throw new ArgumentException(
                $"El título no puede exceder {MaxTitleLength} caracteres.", nameof(title));
        }

        return clean;
    }
}
