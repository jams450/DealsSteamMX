#nullable enable
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deals.API.Controllers;
using Deals.API.Models.Wishlist;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The per-row price timestamp of the wishlist (user request: «la etiqueta de 72 se puede colocar en wishlist
/// tambien»). The wishlist rows now carry the observation date of the SAME offer whose price they already
/// report: <c>bestOfficialObservedAt</c> belongs to whatever won the cheapest legitimate price — an ITAD/gg.deals
/// offer or the direct Steam snapshot — and <c>bestKeyshopObservedAt</c> belongs to the winning keyshop offer.
///
/// Everything here runs offline: the real controller runs over an in-memory <see cref="AppDbContext"/> behind
/// the real <see cref="Repository"/>, so a wrong date is an observation, not a guess. The dates are compared
/// against three different stamps on purpose, because confusing them is the whole risk:
/// <list type="bullet">
/// <item><c>game_offers.observed_at</c> — when that offer's price was seen (the answer);</item>
/// <item><c>steam_games.offers_refreshed_at</c> / <c>ggdeals_refreshed_at</c> — when the provider was last
/// consulted (NOT the answer, even though it is the fresher and more tempting number);</item>
/// <item><c>steam_games.observed_at</c> — the Steam snapshot time, which answers only when Steam itself wins.</item>
/// </list>
/// A price is never rejected for being old here: the date is added, the amount rules are not touched.
/// </summary>
internal static class WishlistPriceObservedAtChecks
{
    private const int Requester = 1;

    // One reference instant: every fixture date is relative to it, so the checks never depend on the clock.
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldOffersRefresh = Now.AddMinutes(-30);
    private static readonly List<string> Failures = [];

    public static void Run()
    {
        var items = LoadWishlist();

        ExternalOfficialWinnerKeepsItsOwnDate(items);
        CheapestOfferDateIsNotTheNewestDate(items);
        KeyshopWinnerKeepsItsOwnDate(items);
        SteamWinnerUsesTheSteamSnapshotDate(items);
        SteamTieWinnerUsesTheSteamSnapshotDate(items);
        MissingPriceHasNoDate(items);
        NonMxnSteamPriceDoesNotCompete(items);
        ExistingPricesAndMetadataAreUnchanged(items);
        PackagePreviewSubtotalsAreUnchanged();
        WireContractIsCamelCaseIsoUtc(items);
        TranslationIsObserved();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} wishlist price-timestamp checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: wishlist price-timestamp checks");
    }

    /// <summary>
    /// An external official offer that beats Steam answers with its own <c>observed_at</c> — not with the
    /// provider consultation stamp, which is fresher and therefore the wrong answer.
    /// </summary>
    private static void ExternalOfficialWinnerKeepsItsOwnDate(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1001];
        CheckEqual(3_000, item.BestOfficialMinor, "1001 the external official price wins");
        CheckEqual("itad", item.BestOfficialSource, "1001 the winner is the ITAD offer");
        CheckEqual(Now.AddDays(-10), item.BestOfficialObservedAt,
            "1001 the row reports the winning offer's own observation date");
        Check(item.BestOfficialObservedAt != OldOffersRefresh,
            "1001 the provider consultation stamp is not reported as the price date");
        Check(item.BestOfficialObservedAt != Now.AddHours(-2),
            "1001 the Steam snapshot date is not reported when an external offer wins");
        CheckEqual(null, item.BestKeyshopObservedAt, "1001 with no keyshop offer there is no keyshop date");
    }

    /// <summary>
    /// The date must belong to the <em>cheapest</em> offer, not to the newest one. A fresh expensive offer next
    /// to an old cheap one would otherwise report a recent date over an old price, which is the mislabel that
    /// makes the ≤72h/+72h badge lie.
    /// </summary>
    private static void CheapestOfferDateIsNotTheNewestDate(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1002];
        CheckEqual(3_000, item.BestOfficialMinor, "1002 the old cheap offer is still the cheapest price");
        CheckEqual(Now.AddDays(-10), item.BestOfficialObservedAt,
            "1002 the date is the cheapest offer's, not the newest offer's");
        Check(item.BestOfficialObservedAt != Now.AddMinutes(-5),
            "1002 a fresh expensive offer does not lend its date to the cheap winner");
        Check(item.BestOfficialObservedAt is not null,
            "1002 an old price is reported with its date instead of being dropped for being old");
    }

    /// <summary>The keyshop column carries the keyshop winner's own date, independently of the official one.</summary>
    private static void KeyshopWinnerKeepsItsOwnDate(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1003];
        CheckEqual(1_656, item.BestKeyshopMinor, "1003 the keyshop offer is the cheapest keyshop");
        CheckEqual("k4g", item.BestKeyshopSource, "1003 the keyshop winner is the keyshop offer");
        CheckEqual(Now.AddDays(-3), item.BestKeyshopObservedAt, "1003 the keyshop date is that offer's own date");
        CheckEqual(20_000, item.BestOfficialMinor, "1003 the official column is unaffected by the keyshop winner");
        CheckEqual(Now.AddHours(-5), item.BestOfficialObservedAt,
            "1003 the two columns keep their own dates, one per winner");
    }

    /// <summary>Steam is not a <c>game_offers</c> row: when its snapshot wins, its own observation date answers.</summary>
    private static void SteamWinnerUsesTheSteamSnapshotDate(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1004];
        CheckEqual(5_999, item.BestOfficialMinor, "1004 the cheaper Steam snapshot wins the official price");
        CheckEqual("steam", item.BestOfficialSource, "1004 the winning source is Steam");
        CheckEqual("regional", item.BestOfficialPricingType, "1004 Steam's pricing type travels with the price");
        CheckEqual(Now.AddHours(-2), item.BestOfficialObservedAt,
            "1004 a Steam win reports the Steam snapshot's own date");
        Check(item.BestOfficialObservedAt != Now.AddHours(-1),
            "1004 the Steam refresh stamp is not reported as the price date");
    }

    /// <summary>
    /// Steam wins ties, and the date must follow the same door the price and the metadata took. A tie that
    /// reported the external offer's date would date a Steam price with another shop's observation.
    /// </summary>
    private static void SteamTieWinnerUsesTheSteamSnapshotDate(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1005];
        CheckEqual(5_000, item.BestOfficialMinor, "1005 a tie keeps the price");
        CheckEqual("steam", item.BestOfficialSource, "1005 Steam wins the tie, as before");
        CheckEqual(Now.AddHours(-4), item.BestOfficialObservedAt,
            "1005 a tie reports the Steam snapshot's date, not the external offer's");
        Check(item.BestOfficialObservedAt != Now.AddDays(-10),
            "1005 the losing external offer lends neither price, nor source, nor date");
    }

    /// <summary>No comparable price means no date: the field is null, never 0, never an invented instant.</summary>
    private static void MissingPriceHasNoDate(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1006];
        CheckEqual(null, item.BestOfficialMinor, "1006 a game with no comparable price has no official price");
        CheckEqual(null, item.BestKeyshopMinor, "1006 a game with no comparable price has no keyshop price");
        CheckEqual(null, item.BestOfficialObservedAt, "1006 no price means no official date");
        CheckEqual(null, item.BestKeyshopObservedAt, "1006 no price means no keyshop date");
        Check(item.ItadSyncedAt is not null,
            "1006 the provider consultation stamp existing does not fabricate a price date");
    }

    /// <summary>
    /// A non-MXN Steam snapshot never competed for the MXN minimum, so it cannot lend its date either.
    /// </summary>
    private static void NonMxnSteamPriceDoesNotCompete(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var item = items[1007];
        CheckEqual(4_500, item.BestOfficialMinor, "1007 a USD Steam price does not compete with the MXN offer");
        CheckEqual("itad", item.BestOfficialSource, "1007 the MXN offer stays the official winner");
        CheckEqual(Now.AddDays(-6), item.BestOfficialObservedAt, "1007 the MXN winner keeps its own date");
    }

    /// <summary>
    /// PRESERVE: nothing else about the row moved. The amounts, the source/label/classification/pricing type and
    /// the unconverted-price exclusion are exactly what they were — the date was only added next to them.
    /// </summary>
    private static void ExistingPricesAndMetadataAreUnchanged(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        var external = items[1001];
        CheckEqual("official", external.BestOfficialClassification, "1001 the classification is unchanged");
        CheckEqual("regional", external.BestOfficialPricingType, "1001 the pricing type is unchanged");
        CheckEqual("ITAD", external.BestOfficialLabel, "1001 the shop label is unchanged");
        CheckEqual(9_900, external.HistoryLowMinor, "1001 the history low is unchanged");

        CheckEqual(3_000, items[1002].BestOfficialMinor,
            "1002 the unconverted offer is still excluded from the comparison");

        var steam = items[1004];
        CheckEqual(null, steam.BestKeyshopMinor, "1004 the keyshop column stays empty");
        CheckEqual("Steam", steam.BestOfficialLabel, "1004 Steam's label still travels with its price");
        CheckEqual(5_000, items[1005].BestOfficialMinor, "1005 the tied price is unchanged");
    }

    /// <summary>
    /// PRESERVE: the package preview reads the same loader, so its subtotals must still be the same amounts.
    /// </summary>
    private static void PackagePreviewSubtotalsAreUnchanged()
    {
        using var db = CreateContext();
        var controller = CreateController(db, Requester);
        var result = controller
            .PackagePreview(new WishlistPackagePreviewRequest([1001, 1003, 1006]), CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var response = result as OkObjectResult;
        Check(response is not null, "preview: the package preview answers 200");
        if (response?.Value is not WishlistPackagePreviewResponse preview)
        {
            Failures.Add("preview: the body is the package preview contract");
            return;
        }

        CheckEqual(23_000L, preview.OfficialSubtotalMinor, "preview: the official subtotal uses the same prices");
        CheckEqual(2, preview.OfficialQuoted, "preview: the priced official games are still counted");
        CheckEqual("1006", string.Join(",", preview.OfficialMissingAppIds), "preview: the unpriced game is missing");
        CheckEqual(1_656L, preview.KeyshopSubtotalMinor, "preview: the keyshop subtotal uses the same prices");
        CheckEqual(1, preview.KeyshopQuoted, "preview: the priced keyshop games are still counted");
    }

    /// <summary>
    /// The wire contract the BFF and the UI read: the two keys are camelCase and the value is ISO-8601 with a
    /// zone (<c>...Z</c> for the UTC instants the database stores), which is what the wishlist contract validates.
    /// A null date is omitted like every other nullable field of this response, so the client sees the key
    /// absent rather than a zero or an epoch.
    /// </summary>
    private static void WireContractIsCamelCaseIsoUtc(IReadOnlyDictionary<int, WishlistItemResponse> items)
    {
        // Mirrors Deals.API/Extensions/MvcExtensions: web defaults (camelCase) plus nulls omitted.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var priced = JsonSerializer.Serialize(items[1001], options);
        Check(priced.Contains("\"bestOfficialObservedAt\"", StringComparison.Ordinal),
            "json: the official date is exposed as bestOfficialObservedAt");
        Check(priced.Contains("\"bestOfficialObservedAt\":\"2026-10-16T12:00:00Z\"", StringComparison.Ordinal),
            "json: the official date is an ISO-8601 UTC instant");
        Check(priced.Contains("\"bestKeyshopObservedAt\"", StringComparison.Ordinal) is false,
            "json: a keyshop date that does not exist is omitted, not zeroed");

        var unpriced = JsonSerializer.Serialize(items[1006], options);
        Check(unpriced.Contains("\"bestOfficialObservedAt\"", StringComparison.Ordinal) is false,
            "json: no comparable price emits no date at all");

        var keyshop = JsonSerializer.Serialize(items[1003], options);
        Check(keyshop.Contains("\"bestKeyshopObservedAt\":\"2026-10-23T12:00:00Z\"", StringComparison.Ordinal),
            "json: the keyshop date is that offer's own UTC instant");
    }

    /// <summary>
    /// The winner projection is rendered by the production provider without a connection: adding the observation
    /// date to the two selected offers must translate, and both winners must project their own
    /// <c>observed_at</c>.
    /// </summary>
    private static void TranslationIsObserved()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=offline-checks;Port=1;Database=deals_checks;Username=checks;Password=checks;Timeout=1")
            .Options;
        using var db = new AppDbContext(options, new OfflineUser());

        string? sql;
        try
        {
            sql = WishlistController
                .PriceWinnerRows(new Repository(db), [9001, 9002])
                .ToQueryString();
            if (Environment.GetEnvironmentVariable("WISHLIST_SQL_DUMP") == "1")
            {
                Console.WriteLine($"=== wishlist/price-winners ===\n{sql}\n");
            }
        }
        catch (Exception exception)
        {
            Failures.Add($"EF translation FAILED for wishlist/price-winners: {exception.GetType().Name}: {exception.Message}");
            return;
        }

        Check(!string.IsNullOrWhiteSpace(sql), "the price-winner query renders SQL");
        Check(Has(sql, "game_offers"), "the price-winner query reads game_offers");
        Check(CountOf(sql, "observed_at") >= 2,
            "both winning offers project their own observation date in SQL");
        Check(Has(sql, "ORDER BY"), "the winner selection still keeps its deterministic ordering");
        Check(Has(sql, "ROW_NUMBER() OVER"), "the winner selection is still one row per offer kind");
    }

    // --- Fixture ----------------------------------------------------------

    private static IReadOnlyDictionary<int, WishlistItemResponse> LoadWishlist()
    {
        using var db = CreateContext();
        var controller = CreateController(db, Requester);
        var result = controller
            .Get(new WishlistQueryRequest { PageSize = 100 }, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (result is not OkObjectResult { Value: WishlistResponse response })
        {
            Failures.Add($"the wishlist answers 200 with its contract, got {result?.GetType().Name ?? "null"}");
            return new Dictionary<int, WishlistItemResponse>();
        }

        var items = response.Items.ToDictionary(item => item.AppId);
        CheckEqual(7, items.Count, "the fixture exposes one row per wished game");
        return items;
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"wishlist-price-timestamps-{Guid.NewGuid():N}")
            .Options;

        var db = new AppDbContext(options, new OfflineUser());
        Seed(db);
        return db;
    }

    private static void Seed(AppDbContext db)
    {
        db.Users.Add(new User
        {
            UserId = Requester,
            Name = "wishlist",
            Email = "wishlist-checks@example.test",
            Password = "not-a-real-password",
            SteamId64 = "76561198000000000",
            WishlistSyncedAt = Now.AddHours(-1),
            WishlistState = WishlistStates.Ok,
        });

        // 1001: an external official offer beats Steam; the provider was consulted after that offer was seen.
        Steam(db, 1001, currentPriceMinor: 39_900, steamObservedAt: Now.AddHours(-2),
            offersRefreshedAt: OldOffersRefresh);
        Offer(db, 1001, "itad", "official", 3_000, Now.AddDays(-10), historyLowMinor: 9_900);
        Offer(db, 1001, "ggdeals", "official", 30_000, Now.AddMinutes(-5));

        // 1002: an old cheap offer next to a fresh expensive one, plus an unconverted amount that never counts.
        Steam(db, 1002, currentPriceMinor: null, steamObservedAt: Now.AddHours(-3),
            offersRefreshedAt: OldOffersRefresh);
        Offer(db, 1002, "itad", "official", 3_000, Now.AddDays(-10));
        Offer(db, 1002, "ggdeals", "official", 30_000, Now.AddMinutes(-5));
        Offer(db, 1002, "itad-unconverted", "official", 100, Now, pricingType: "unconverted");

        // 1003: an official winner and a cheaper keyshop winner, each with its own date.
        Steam(db, 1003, currentPriceMinor: null, steamObservedAt: Now.AddHours(-4),
            offersRefreshedAt: OldOffersRefresh);
        Offer(db, 1003, "itad", "official", 20_000, Now.AddHours(-5));
        Offer(db, 1003, "k4g", "keyshop", 1_656, Now.AddDays(-3));

        // 1004: Steam is strictly cheaper. The refresh stamp is deliberately different from the snapshot date.
        Steam(db, 1004, currentPriceMinor: 5_999, steamObservedAt: Now.AddHours(-2),
            offersRefreshedAt: Now.AddHours(-1));
        Offer(db, 1004, "itad", "official", 20_000, Now.AddDays(-10));

        // 1005: Steam ties the external offer, and keeps the tie as before.
        Steam(db, 1005, currentPriceMinor: 5_000, steamObservedAt: Now.AddHours(-4),
            offersRefreshedAt: Now.AddHours(-1));
        Offer(db, 1005, "itad", "official", 5_000, Now.AddDays(-10));

        // 1006: no comparable price at all, but the provider stamps exist.
        Steam(db, 1006, currentPriceMinor: null, steamObservedAt: Now.AddHours(-5),
            offersRefreshedAt: OldOffersRefresh);

        // 1007: a USD Steam price is not comparable to the MXN offers.
        Steam(db, 1007, currentPriceMinor: 1_999, steamObservedAt: Now.AddHours(-6),
            offersRefreshedAt: OldOffersRefresh, currency: "USD");
        Offer(db, 1007, "itad", "official", 4_500, Now.AddDays(-6));

        foreach (var appId in new[] { 1001, 1002, 1003, 1004, 1005, 1006, 1007 })
        {
            db.UserLibrary.Add(new UserLibrary
            {
                UserLibraryId = appId,
                UserId = Requester,
                Store = "steam",
                StoreGameId = appId.ToString(),
                Title = $"Juego {appId}",
                State = LibraryStates.Wished,
                ImportedAt = Now.AddDays(-30),
            });
        }

        db.SaveChanges();
    }

    private static void Steam(
        AppDbContext db,
        int appId,
        int? currentPriceMinor,
        DateTime steamObservedAt,
        DateTime offersRefreshedAt,
        string currency = "MXN") =>
        db.SteamGames.Add(new SteamGame
        {
            SteamGameId = SteamGameIdFor(appId),
            AppId = appId,
            Region = "mx",
            Name = $"Juego {appId}",
            Currency = currency,
            InitialPriceMinor = currentPriceMinor is null ? null : currentPriceMinor + 5_000,
            CurrentPriceMinor = currentPriceMinor,
            ObservedAt = steamObservedAt,
            OffersRefreshedAt = offersRefreshedAt,
            GgDealsRefreshedAt = Now.AddDays(-2),
            EpicRefreshedAt = Now.AddDays(-2),
            MicrosoftRefreshedAt = Now.AddDays(-2),
        });

    private static void Offer(
        AppDbContext db,
        int appId,
        string source,
        string classification,
        int priceMinor,
        DateTime observedAt,
        string pricingType = "regional",
        int? historyLowMinor = null) =>
        db.GameOffers.Add(new GameOffer
        {
            GameOfferId = NextOfferId(),
            SteamGameId = SteamGameIdFor(appId),
            Region = "mx",
            Source = source,
            OfferKey = source,
            ShopId = source,
            ShopName = classification == "keyshop" ? source : "ITAD",
            Classification = classification,
            OriginalCurrency = "MXN",
            OriginalRegularPriceMinor = priceMinor,
            OriginalCurrentPriceMinor = priceMinor,
            MxnRegularPriceMinor = priceMinor,
            MxnCurrentPriceMinor = priceMinor,
            HistoryLowAllMinor = historyLowMinor,
            HistoryLowCurrency = historyLowMinor is null ? null : "MXN",
            PricingType = pricingType,
            ObservedAt = observedAt,
        });

    // Offers hang off the Steam snapshot of the same appid, which is how the controller aggregates them.
    private static int SteamGameIdFor(int appId) => 2_000 + appId;

    private static long _nextOfferId;

    private static long NextOfferId() => ++_nextOfferId;

    private static WishlistController CreateController(AppDbContext db, int userId)
    {
        var controller = new WishlistController(
            new Repository(db),
            new UnusedSyncService(),
            new UnusedAcquisitionService(),
            new JobRunLog(new Repository(db)),
            NullLogger<WishlistController>.Instance,
            db);

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "offline");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return controller;
    }

    // --- Helpers ----------------------------------------------------------

    private static void Check(bool value, string name)
    {
        if (!value)
        {
            Failures.Add(name);
        }
    }

    private static void CheckEqual<T>(T? expected, T? actual, string name)
    {
        if (!EqualityComparer<T?>.Default.Equals(expected, actual))
        {
            Failures.Add($"{name}: expected '{expected}', got '{actual}'");
        }
    }

    private static bool Has(string? sql, string marker) =>
        sql is not null && sql.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static int CountOf(string? sql, string marker)
    {
        if (sql is null)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = sql.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private sealed class UnusedSyncService : IWishlistSyncService
    {
        public Task<WishlistListSyncReport> SyncListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("the read-only wishlist checks never sync");

        public Task<WishlistRefreshReport> RefreshWishedGamesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("the read-only wishlist checks never refresh");
    }

    private sealed class UnusedAcquisitionService : IWishlistAcquisitionService
    {
        public Task<WishlistAcquisitionResult?> AcquireAsync(
            int userId,
            int appId,
            WishlistAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the read-only wishlist checks never acquire");
    }

    private sealed class OfflineUser : ICurrentUserService
    {
        public int? GetUserId() => null;

        public int GetRequiredUserId() => throw new InvalidOperationException("wishlist checks have no request user");

        public int? GetSessionVersion() => null;

        public Guid? GetSessionId() => null;

        public string GetName() => "wishlist-checks";

        public string GetEmail() => "wishlist-checks@example.test";

        public bool IsAdmin() => false;
    }
}
