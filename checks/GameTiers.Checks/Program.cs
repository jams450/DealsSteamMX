using System.Globalization;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using GameTiersChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

// Deterministic checks for the per-review annual tier list (specs S3-S6, task T1).
//
// Rule: a tier card is one reviewed play, not one game. The old key (user, year, game) collapsed two
// playthroughs of the same game into one card, and the migration designed on top of it copied only the
// newest review — which silently dropped the other play's placement. These checks pin the replacement:
//   1. the year list is derived per review (finished month, else started month), distinct and newest first,
//   2. a year returns one card per dated review of the caller, with platform and GOTY intact,
//   3. two reviews of the same game (even on the same platform) are ranked and saved independently,
//   4. a save refuses another user's review and a review dated in another year, leaving stored data unchanged,
//   5. the EF model, the schema and the manual migration agree on the review-keyed primary key,
//   6. the merge repoints the tier anchor by game_id and no longer deletes "colliding" rows, because the
//      review id makes a collision impossible.
// No PostgreSQL server is contacted, no credential is used and no real migration is applied: the schema and
// migration evidence is read-only text, the behaviour runs against EF's in-memory provider.
//
// A non-zero exit or a thrown exception marks the check suite failed.

var failures = new List<string>();
void Check(bool value, string name)
{
    if (!value) failures.Add(name);
}

static async Task<Exception?> Capture(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

// A compact, order-sensitive description of what the service returns: every observable field of a card.
static string Describe(IReadOnlyList<GameTierEntry> entries) =>
    string.Join(" | ", entries.Select(entry =>
        $"{entry.ReviewId}:{entry.Title}:{entry.Platform}:{(entry.IsGoty ? "goty" : "plain")}:{entry.Tier}@{entry.Position}"));

var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseInMemoryDatabase($"game-tiers-checks-{Guid.NewGuid():N}")
    .Options;

await using var context = new AppDbContext(dbOptions, new StubCurrentUserService());

context.Users.AddRange(
    new User { UserId = 1, Name = "Owner", Email = "owner@example.com", Password = "x", Active = true, SessionVersion = 1 },
    new User { UserId = 2, Name = "Other", Email = "other@example.com", Password = "x", Active = true, SessionVersion = 1 });
context.Games.AddRange(
    new Game { GameId = 7, Title = "Portal", NormalizedTitle = "portal" },
    new Game { GameId = 8, Title = "Hades", NormalizedTitle = "hades" });

// Every fixture is a dated play. `r5` has no date on purpose: it has no year, so it belongs to no list.
context.GameReviews.AddRange(
    Review(1, 1, 7, "steam", started: null, finished: "2026-03-01", goty: true),
    Review(2, 1, 7, "ps5", started: "2026-01-01", finished: null, goty: false),
    Review(3, 1, 8, "steam", started: "2026-02-01", finished: null, goty: false),
    Review(4, 1, 7, "steam", started: null, finished: "2025-11-01", goty: false),
    Review(5, 1, 8, "ps5", started: null, finished: null, goty: false),
    Review(6, 2, 7, "steam", started: null, finished: "2026-05-01", goty: false),
    // Replay of r1's exact game and platform: a distinct play, so a distinct card.
    Review(7, 1, 7, "steam", started: "2026-04-01", finished: null, goty: false));
await context.SaveChangesAsync();

var service = new GameTierService(new InMemoryRepository(context));

// ── S3: years come from dated reviews, one option per year ───────────────────────────────────────────
// r4 is the same game as the 2026 reviews but was played in 2025. A per-game (latest review only) query
// would answer [2026] and hide the 2025 list; a per-review query keeps both.
var years = await service.GetYearsAsync(1);
Check(years.SequenceEqual([2026, 2025]), $"years: caller's dated reviews give [2026, 2025] newest first (got [{string.Join(',', years)}])");

var otherYears = await service.GetYearsAsync(2);
Check(otherYears.SequenceEqual([2026]), $"years: another user's list only holds their own reviews (got [{string.Join(',', otherYears)}])");

Check((await service.GetYearsAsync(3)).Count == 0, "years: a user without reviews gets an empty list, not the current year");

// ── S3/S5: one card per dated review, with platform and GOTY ────────────────────────────────────────
var unranked2026 = await service.GetForYearAsync(1, 2026);
Check(unranked2026.Count == 4, $"cards: 2026 has four dated reviews (got {unranked2026.Count})");
Check(Describe(unranked2026) == "3:Hades:steam:plain:Unranked@2147483647 | 1:Portal:steam:goty:Unranked@2147483647 | 2:Portal:ps5:plain:Unranked@2147483647 | 7:Portal:steam:plain:Unranked@2147483647",
    $"cards: one card per review with platform, GOTY and unranked order preserved (got '{Describe(unranked2026)}')");
Check(unranked2026.Single(entry => entry.ReviewId == 1).IsGoty, "cards: the GOTY flag of the review reaches its card");
Check(unranked2026.Single(entry => entry.ReviewId == 2).Platform == "ps5", "cards: a replay on another platform keeps that platform");
Check((await service.GetForYearAsync(1, 2025)).Single().ReviewId == 4, "cards: the 2025 list holds the 2025 play of the same game");

// ── S5: independent ranks for a replay, including the same platform ─────────────────────────────────
Check(await service.SetAsync(1, 2026, 1, "S", 0), "save: the first play is ranked");
Check(await service.SetAsync(1, 2026, 2, "A", 0), "save: the other-platform replay is ranked on its own");
Check(await service.SetAsync(1, 2026, 7, "B", 0), "save: the same-platform replay is ranked on its own");

var ranked2026 = await service.GetForYearAsync(1, 2026);
Check(Describe(ranked2026) == "1:Portal:steam:goty:S@0 | 2:Portal:ps5:plain:A@0 | 7:Portal:steam:plain:B@0 | 3:Hades:steam:plain:Unranked@2147483647",
    $"independent ranks: three plays of two games keep three different placements (got '{Describe(ranked2026)}')");

// Existing behaviour that must survive: editing a placement updates the same row instead of adding a
// second card for the same review.
Check(await service.SetAsync(1, 2026, 1, "F", 2), "preserve: an existing card can be moved again");
var moved = await service.GetForYearAsync(1, 2026);
Check(moved.Count(entry => entry.ReviewId == 1) == 1, "preserve: moving a card never duplicates it");
Check(moved.Single(entry => entry.ReviewId == 1).Tier == "F" && moved.Single(entry => entry.ReviewId == 1).Position == 2,
    "preserve: the second move persists tier and position");

// ── S5: ownership and year are refused server-side, leaving stored data unchanged ───────────────────
var beforeRefusals = Describe(await service.GetForYearAsync(1, 2026));

Check(!await service.SetAsync(2, 2026, 1, "S", 0), "refusal: another user's review cannot be ranked");
Check(!await service.SetAsync(1, 2026, 4, "S", 0), "refusal: a review dated 2025 cannot enter the 2026 list");
Check(!await service.SetAsync(1, 2026, 5, "S", 0), "refusal: an undated review belongs to no year and cannot be ranked");
Check(await service.SetAsync(2, 2026, 6, "S", 0), "control: the owner can rank their own review");
Check(Describe(await service.GetForYearAsync(1, 2026)) == beforeRefusals,
    "refusal: rejected saves leave every stored card exactly as it was");
Check(Describe(await service.GetForYearAsync(2, 2026)) == "6:Portal:steam:plain:S@0",
    $"refusal: the other user's failed attempt wrote nothing into their own list (got '{Describe(await service.GetForYearAsync(2, 2026))}')");

// A refused save must also not have created the 2025 placement it would have used.
Check(await service.SetAsync(1, 2025, 4, "C", 1), "control: the same review can be ranked in its own year");
Check((await service.GetForYearAsync(1, 2025)).Single().Tier == "C", "control: the 2025 placement is the one that was written");

// Existing validation survives: an invalid scope or tier is a bad request, not an empty success.
Check(await Capture(() => service.GetForYearAsync(0, 2026)) is ArgumentException, "preserve: a non-positive user is rejected");
Check(await Capture(() => service.GetForYearAsync(1, 999)) is ArgumentException, "preserve: an out-of-range year is rejected");
Check(await Capture(() => service.SetAsync(1, 2026, 1, "E", 0)) is ArgumentException, "preserve: a tier outside S–F is rejected");
Check(await Capture(() => service.SetAsync(1, 2026, 1, "S", -1)) is ArgumentException, "preserve: a negative position is a bad request");
Check(await Capture(() => service.SetAsync(1, 2026, 1, "S", 10001)) is ArgumentException, "preserve: position above 10000 is a bad request");
Check((await service.GetForYearAsync(1, 2026)).Single(entry => entry.ReviewId == 1).Tier == "F",
    "preserve: rejected positions leave the stored placement untouched");

// ── S5/S6: the EF model matches the review-keyed design ─────────────────────────────────────────────
var tierEntity = context.Model.FindEntityType(typeof(UserGameTier));
var primaryKey = tierEntity?.FindPrimaryKey()?.Properties.Select(property => property.Name).ToArray() ?? [];
Check(primaryKey.SequenceEqual(["UserId", "Year", "GameReviewId"]),
    $"model: the placement key is (UserId, Year, GameReviewId) (got [{string.Join(',', primaryKey)}])");

Check(tierEntity?.FindProperty("GameId") is not null, "model: game_id stays as the merge anchor");
Check(tierEntity?.FindProperty("GameId")?.IsNullable == false, "model: the merge anchor is required");

static string? ForeignKeyTarget(IEntityType? entity, string property) =>
    entity?.GetForeignKeys()
        .FirstOrDefault(key => key.Properties.Count == 1 && key.Properties[0].Name == property)?.PrincipalEntityType.Name;

Check(ForeignKeyTarget(tierEntity, "GameReviewId") == typeof(GameReview).FullName,
    "model: game_review_id is a foreign key to GameReview");
Check(tierEntity?.GetForeignKeys().FirstOrDefault(key => key.Properties.Count == 1 && key.Properties[0].Name == "GameReviewId")?.DeleteBehavior == DeleteBehavior.Cascade,
    "model: deleting a review cascades to its placement");
Check(ForeignKeyTarget(tierEntity, "GameId") == typeof(Game).FullName, "model: game_id remains a foreign key to Game");
Check(tierEntity?.GetForeignKeys().FirstOrDefault(key => key.Properties.Count == 1 && key.Properties[0].Name == "GameId")?.DeleteBehavior == DeleteBehavior.Cascade,
    "model: deleting a game cascades to its placements");

// ── S6: schema, migration, merge and the read-only invariant file agree ─────────────────────────────
var root = RepositoryRoot();
Check(root is not null, "files: the repository root with SQL/schema.sql is reachable");
if (root is not null)
{
    var schema = File.ReadAllText(Path.Combine(root, "SQL", "schema.sql"));
    var schemaTable = TableBlock(schema, "user_game_tiers");
    Check(schemaTable.Contains("game_review_id BIGINT NOT NULL REFERENCES game_reviews(game_review_id) ON DELETE CASCADE", StringComparison.Ordinal),
        "schema: game_review_id is NOT NULL and cascades from game_reviews");
    Check(schemaTable.Contains("PRIMARY KEY (user_id, year, game_review_id)", StringComparison.Ordinal),
        "schema: the placement key is (user_id, year, game_review_id)");
    Check(!schemaTable.Contains("PRIMARY KEY (user_id, year, game_id)", StringComparison.Ordinal),
        "schema: the game-keyed primary key is gone");
    Check(schemaTable.Contains("game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE", StringComparison.Ordinal),
        "schema: the merge anchor still cascades from games");

    var migration = File.ReadAllText(Path.Combine(root, "SQL", "migrations", "2026-10-20_user_game_tiers_per_review.sql"));
    var backfill = StatementAfter(migration, "INSERT INTO public.user_game_tiers");
    Check(backfill.Contains("JOIN public.game_reviews", StringComparison.Ordinal) && !backfill.Contains("DISTINCT ON", StringComparison.Ordinal),
        "migration: the backfill copies each old placement to every matching review instead of picking one");
    Check(migration.Contains("WHERE t.game_review_id IS NULL", StringComparison.Ordinal),
        "migration: only the not-yet-expanded placements are copied");
    Check(migration.Contains("DELETE FROM public.user_game_tiers", StringComparison.Ordinal)
          && migration.Contains("WHERE game_review_id IS NULL", StringComparison.Ordinal),
        "migration: orphan placements (no matching dated review) are the only rows deleted");
    Check(migration.Contains("PRIMARY KEY (user_id, year, game_review_id)", StringComparison.Ordinal),
        "migration: the new primary key is created");
    Check(migration.Contains("ADD COLUMN IF NOT EXISTS game_review_id BIGINT", StringComparison.Ordinal),
        "migration: the new column is added defensively");

    var merge = File.ReadAllText(Path.Combine(root, "Deals.BusinessLogic", "Services", "GameMergeService.cs"));
    Check(merge.Contains("UPDATE public.user_game_tiers SET game_id = {0}", StringComparison.Ordinal),
        "merge: the tier anchor is repointed to the survivor");
    Check(!merge.Contains("DELETE FROM public.user_game_tiers", StringComparison.Ordinal),
        "merge: no tier collision delete remains (review ids cannot collide across games)");

    var invariants = File.ReadAllText(Path.Combine(root, "SQL", "checks", "game_tiers_reviews.sql"));
    Check(invariants.Contains("r.user_id", StringComparison.Ordinal) && invariants.Contains("r.game_id", StringComparison.Ordinal),
        "checks: the SQL invariant compares the placement owner and game against its review");
    Check(!invariants.Contains("INSERT ", StringComparison.OrdinalIgnoreCase)
          && !invariants.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase)
          && !invariants.Contains("DELETE ", StringComparison.OrdinalIgnoreCase),
        "checks: the invariant file stays read-only");
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: per-review annual tiers, year list, ownership/year refusal and review-keyed integrity");
return 0;

static GameReview Review(long reviewId, int userId, long gameId, string platform, string? started, string? finished, bool goty) =>
    new()
    {
        GameReviewId = reviewId,
        UserId = userId,
        GameId = gameId,
        Platform = platform,
        StartedMonth = started is null ? null : DateTime.ParseExact(started, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        FinishedMonth = finished is null ? null : DateTime.ParseExact(finished, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        IsGoty = goty,
        Status = "finished"
    };

// Reads the CREATE TABLE block so a column of another table can never satisfy a schema assertion.
static string TableBlock(string schema, string table)
{
    var start = schema.IndexOf($"CREATE TABLE {table} (", StringComparison.Ordinal);
    if (start < 0) return string.Empty;
    var end = schema.IndexOf("\n);", start, StringComparison.Ordinal);
    return end < 0 ? schema[start..] : schema[start..end];
}

// Reads a single statement from a migration, so a comment about a rejected design cannot satisfy the
// assertion for the statement itself.
static string StatementAfter(string sql, string marker)
{
    var start = sql.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0) return string.Empty;
    var end = sql.IndexOf(';', start);
    return end < 0 ? sql[start..] : sql[start..end];
}

static string? RepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SQL", "schema.sql")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return null;
}
