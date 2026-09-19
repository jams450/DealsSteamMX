using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Batch price-binding resolution for the library. Mirrors the wishlist aggregate semantics (region-scoped
/// Steam snapshots, MXN-only minima, keyshop split) but resolves each row first by exact identity and then
/// by a unique normalized-title candidate.
/// </summary>
public class LibraryPriceBindingService : ILibraryPriceBindingService
{
    private const string SteamStore = "steam";
    private const string SteamNamespace = "steam";

    // Same region the rest of the code reads and writes.
    private const string Region = "mx";

    // game_offers bands: keyshop is the grey-market band, every other classification is a legitimate shop.
    private const string KeyshopClassification = "keyshop";

    // An offer without a derived MXN amount cannot take part in an MXN comparison.
    private const string UnconvertedPricing = "unconverted";

    // Currency of the history low. Providers persist uppercase ISO codes (see SteamGameService).
    private const string MxnCurrency = "MXN";

    private readonly IRepository _repository;

    public LibraryPriceBindingService(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyDictionary<long, LibraryPriceBinding>> ResolveAsync(
        IReadOnlyList<UserLibrary> rows,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, LibraryPriceBinding>(rows.Count);
        if (rows.Count == 0)
        {
            return result;
        }

        // Step 1: subscription is a real state, never a lookup. Everything else is a candidate.
        var candidates = new List<UserLibrary>(rows.Count);
        foreach (var row in rows)
        {
            if (row.State == LibraryStates.Subscription)
            {
                result[row.UserLibraryId] = LibraryPriceBinding.Subscription;
                continue;
            }

            candidates.Add(row);
        }

        if (candidates.Count == 0)
        {
            return result;
        }

        // Step 2a: exact Steam through the row's own store id.
        var resolutions = new Dictionary<long, Resolution>(candidates.Count);
        foreach (var row in candidates)
        {
            var directAppId = row.Store == SteamStore ? ParseAppId(row.StoreGameId) : 0;
            if (directAppId > 0)
            {
                resolutions[row.UserLibraryId] = new Resolution(directAppId, LibraryBindingSources.Steam, IsTitleCandidate: false);
            }
        }

        // Step 2b: exact Steam through the row's canonical game id -> ('steam', appid). One set-based query.
        var gameIds = candidates
            .Where(row => row.GameId is > 0)
            .Select(row => row.GameId!.Value)
            .Distinct()
            .ToList();
        var appIdByGameId = new Dictionary<long, int>();
        if (gameIds.Count > 0)
        {
            var steamMappings = await _repository.Get<GameExternalId>()
                .Where(mapping => gameIds.Contains(mapping.GameId) &&
                    mapping.NamespaceName == SteamNamespace)
                .Select(mapping => new { mapping.GameId, mapping.ExternalId })
                .ToListAsync(cancellationToken);

            foreach (var mapping in steamMappings)
            {
                var appId = ParseAppId(mapping.ExternalId);
                if (appId > 0 && !appIdByGameId.ContainsKey(mapping.GameId))
                {
                    appIdByGameId[mapping.GameId] = appId;
                }
            }
        }

        foreach (var row in candidates)
        {
            if (resolutions.ContainsKey(row.UserLibraryId))
            {
                continue;
            }

            if (row.GameId is > 0 && appIdByGameId.TryGetValue(row.GameId.Value, out var mappedAppId))
            {
                resolutions[row.UserLibraryId] = new Resolution(mappedAppId, LibraryBindingSources.Steam, IsTitleCandidate: false);
            }
        }

        // Step 4 (batch): every unresolved candidate title is normalized and matched in one set-based query
        // against canonical games that own an exact ('steam', appid). Matches are applied after steps 2a-2b.
        var unresolved = candidates
            .Where(row => !resolutions.ContainsKey(row.UserLibraryId))
            .ToList();

        var titles = unresolved
            .Select(row => GameTitleNormalizer.Normalize(row.Title))
            .Where(title => title.Length > 0)
            .Distinct()
            .ToList();

        var candidateAppIdsByTitle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var titleCandidateAppIds = new HashSet<int>();
        if (titles.Count > 0)
        {
            var matches = await (
                    from game in _repository.Get<Game>()
                    join externalId in _repository.Get<GameExternalId>()
                        on game.GameId equals externalId.GameId
                    where titles.Contains(game.NormalizedTitle) &&
                        externalId.NamespaceName == SteamNamespace
                    select new { game.NormalizedTitle, externalId.ExternalId })
                .Distinct()
                .ToListAsync(cancellationToken);

            candidateAppIdsByTitle = matches
                .GroupBy(match => match.NormalizedTitle, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(match => ParseAppId(match.ExternalId))
                        .Where(appId => appId > 0)
                        .Distinct()
                        .ToList(),
                    StringComparer.Ordinal);

            foreach (var appId in candidateAppIdsByTitle.Values.SelectMany(appIds => appIds))
            {
                titleCandidateAppIds.Add(appId);
            }
        }

        // One region-scoped steam_games query covers the direct appids, the game-id appids and the title
        // candidates. No per-row lookup.
        var appIdsToLoad = new HashSet<int>();
        foreach (var resolution in resolutions.Values)
        {
            appIdsToLoad.Add(resolution.AppId);
        }

        appIdsToLoad.UnionWith(titleCandidateAppIds);

        var appIdToGame = new Dictionary<int, SteamGame>();
        if (appIdsToLoad.Count > 0)
        {
            var snapshots = await _repository.Get<SteamGame>()
                .Where(game => game.Region == Region && appIdsToLoad.Contains(game.AppId))
                .ToListAsync(cancellationToken);

            foreach (var snapshot in snapshots)
            {
                if (!appIdToGame.ContainsKey(snapshot.AppId))
                {
                    appIdToGame[snapshot.AppId] = snapshot;
                }
            }
        }

        // Step 4: apply the unique candidate only to rows still unresolved. Exactly one distinct appid.
        foreach (var row in unresolved)
        {
            if (resolutions.ContainsKey(row.UserLibraryId))
            {
                continue;
            }

            var normalized = GameTitleNormalizer.Normalize(row.Title);
            if (normalized.Length == 0 ||
                !candidateAppIdsByTitle.TryGetValue(normalized, out var appIds) ||
                appIds.Count != 1)
            {
                continue;
            }

            resolutions[row.UserLibraryId] = new Resolution(appIds[0], LibraryBindingSources.Title, IsTitleCandidate: true);
        }

        // One grouped aggregate query for every resolved steam game, exactly the wishlist pattern.
        //  - HistoryLowMinor: lowest history_low_all_minor recorded in MXN (mixed currencies are not comparable).
        //  - BestOfficialMinor: cheapest current MXN price among non-keyshop offers.
        //  - BestKeyshopMinor: cheapest current MXN price among keyshop offers.
        // Rows with pricing_type "unconverted" are excluded; nulls are ignored by the minima and 0 stays real.
        var steamGameIds = appIdToGame.Values
            .Select(game => game.SteamGameId)
            .Distinct()
            .ToList();
        var aggregatesByGameId = new Dictionary<int, Aggregate>();
        if (steamGameIds.Count > 0)
        {
            var aggregates = await _repository.Get<GameOffer>()
                .Where(offer => steamGameIds.Contains(offer.SteamGameId))
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
                .ToListAsync(cancellationToken);

            aggregatesByGameId = aggregates.ToDictionary(
                aggregate => aggregate.SteamGameId,
                aggregate => new Aggregate(
                    aggregate.HistoryLowMinor,
                    aggregate.BestOfficialMinor,
                    aggregate.BestKeyshopMinor));
        }

        foreach (var row in candidates)
        {
            if (!resolutions.TryGetValue(row.UserLibraryId, out var resolution))
            {
                result[row.UserLibraryId] = LibraryPriceBinding.None;
                continue;
            }

            appIdToGame.TryGetValue(resolution.AppId, out var game);

            Aggregate? aggregate = null;
            var hasAggregate = game is not null && aggregatesByGameId.TryGetValue(game.SteamGameId, out aggregate);

            // A candidate that yields no price is not a candidate: display-only steps never label "none" rows.
            if (resolution.IsTitleCandidate && !hasAggregate)
            {
                result[row.UserLibraryId] = LibraryPriceBinding.None;
                continue;
            }

            result[row.UserLibraryId] = new LibraryPriceBinding(
                resolution.IsTitleCandidate ? LibraryPriceStates.TitleCandidate : LibraryPriceStates.Exact,
                resolution.Source,
                resolution.AppId,
                aggregate?.BestOfficialMinor,
                aggregate?.BestKeyshopMinor,
                aggregate?.HistoryLowMinor,
                game?.InitialPriceMinor,
                game?.Currency);
        }

        return result;
    }

    private static int ParseAppId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) && appId > 0
            ? appId
            : 0;

    private sealed record Resolution(int AppId, string Source, bool IsTitleCandidate);

    private sealed record Aggregate(int? HistoryLowMinor, int? BestOfficialMinor, int? BestKeyshopMinor);
}
