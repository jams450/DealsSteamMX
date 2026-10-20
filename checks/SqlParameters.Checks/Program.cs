using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.GgDeals;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

// Deterministic, no-database checks for spec S1 (task T1): a nullable provider field must reach the
// database as an explicitly typed NULL, never as a bare DBNull value.
//
// Reported failure (2026-10-19, job bundles.global-sync, scheduled, 1410 ms):
//   {"error":"The current provider doesn't have a store type mapping for properties of type 'DBNull'."}
//
// Mechanism: EF Core 9 infers a store type for every raw-SQL argument from the runtime type of its value,
// and it has no mapping for DBNull. The writers bound a missing provider field as
// "(object?)value ?? DBNull.Value", so the very first bundle whose shop, page URL or dates were absent
// aborted the job inside command construction, before any row was written. The transaction made that a
// clean failure (nothing is stored), but the snapshot was never refreshed.
//
// Why this harness: it runs the production statements and the production binding arrays through EF Core
// with the real Npgsql provider, but against a capturing DbConnection instead of a server. EF therefore
// performs the exact same parameter-to-command translation it performs in production (placeholder
// substitution, parameter list, store types) and the check inspects the built command. No connection is
// opened, no SQL executes, no provider is contacted and no production sync or write happens.
//
// Positive control: the first check pushes an untyped DBNull through this same harness and requires the
// reported message verbatim. If that control ever stops failing, the harness no longer measures the bug
// and every other check below proves nothing.

var failures = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        failures.Add(name);
    }
}

// Captures the DbCommand EF builds, then reports either the built command or the construction failure.
async Task<BuiltCommand> BuildAsync(string statement, object[] bindings)
{
    var connection = new CaptureConnection();
    var options = new DbContextOptionsBuilder<CaptureContext>().UseNpgsql(connection).Options;
    await using var context = new CaptureContext(options);
    try
    {
        await context.Database.ExecuteSqlRawAsync(statement, bindings);
    }
    catch (Exception exception)
    {
        return new BuiltCommand(string.Empty, [], exception);
    }

    var command = connection.Command ?? throw new InvalidOperationException("EF built no command.");
    return new BuiltCommand(command.Text, command.Capture(), null);
}

// The merge audit insert runs through SqlQueryRaw in production, so it is built through the query path.
async Task<BuiltCommand> BuildQueryAsync(string statement, object[] bindings)
{
    var connection = new CaptureConnection();
    var options = new DbContextOptionsBuilder<CaptureContext>().UseNpgsql(connection).Options;
    await using var context = new CaptureContext(options);
    try
    {
        await context.Database.SqlQueryRaw<MergeAuditRow>(statement, bindings).ToListAsync();
    }
    catch (Exception exception)
    {
        return new BuiltCommand(string.Empty, [], exception);
    }

    var command = connection.Command ?? throw new InvalidOperationException("EF built no command.");
    return new BuiltCommand(command.Text, command.Capture(), null);
}

static string Describe(BuiltCommand command) =>
    command.Failure is null
        ? $"built: {Summarize(command)}"
        : $"{command.Failure.GetType().Name}: {command.Failure.Message.Split('\n')[0]}";

// Slot order matters: EF substitutes {n} with the n-th binding, so the fingerprint locks both the value
// and its position.
static string Summarize(BuiltCommand command) =>
    string.Join("|", command.Parameters.Select(parameter => parameter.Value switch
    {
        DBNull or null => "NULL",
        string text => text,
        DateTime moment => moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? string.Empty,
    }));

static int FirstDifference(string expected, string actual)
{
    var length = Math.Min(expected.Length, actual.Length);
    for (var index = 0; index < length; index++)
    {
        if (expected[index] != actual[index])
        {
            return index;
        }
    }

    return length;
}

static string Window(string text, int index)
{
    var start = Math.Max(0, index - 20);
    return text.Substring(start, Math.Min(text.Length - start, 60));
}

// Slot order matters more than formatting: the fingerprint is compared with the expected literal and a
// mismatch reports where the first difference is, so a drift reads as a location instead of a wall of text.
void CheckFingerprint(string expected, string name, BuiltCommand command)
{
    var actual = Summarize(command);
    if (actual == expected)
    {
        return;
    }

    var index = FirstDifference(expected, actual);
    failures.Add(
        $"{name} (differs at char {index}: expected '{Window(expected, index)}' but got '{Window(actual, index)}')");
}

// The rule under test: every argument of these statements is an explicitly typed parameter, so no null
// can cross EF as an untyped value.
static bool ExplicitlyTyped(object[] bindings) =>
    bindings.All(binding => binding is NpgsqlParameter { NpgsqlDbType: not NpgsqlDbType.Unknown });

static string TypedSummary(object[] bindings) =>
    string.Join("|", bindings.Select(binding => binding switch
    {
        NpgsqlParameter parameter => $"{parameter.ParameterName}:{parameter.NpgsqlDbType}",
        DBNull => "DBNull",
        null => "null",
        var other => other.GetType().Name,
    }));

var observedAt = new DateTime(2026, 10, 19, 10, 13, 15, DateTimeKind.Utc);

// --- 0. Positive control: the harness reproduces the reported failure -------------

var control = await BuildAsync("INSERT INTO probe (observed_at) VALUES ({0})", [DBNull.Value]);
Check(
    control.Failure is InvalidOperationException { Message: var message } &&
    message.Contains("store type mapping for properties of type 'DBNull'", StringComparison.Ordinal),
    $"the harness reproduces the reported bundles.global-sync failure for an untyped DBNull (got {Describe(control)})");

// --- A. Global ITAD bundle snapshot (the job that failed) ------------------------

// A bundle whose provider fields are all absent: no shop, no page URL, no readable deal URL, no details,
// no dates and no tiers. This is the shape that aborted the reported run.
var sparseBundle = new ItadBundle(
    "itad-bundle-sparse",
    "Bundle sin datos opcionales",
    PageUrl: null,
    ShopId: null,
    ShopName: null,
    Details: null,
    PublishedAt: null,
    ExpiresAt: null,
    Url: "http://insecure.example/bundle",
    Tiers: [],
    MatchedItadIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    AttributionComplete: true);

var sparseBundleBindings = EfGlobalBundleSnapshotWriter.BuildUpsertBindings(sparseBundle, observedAt);
Check(
    ExplicitlyTyped(sparseBundleBindings),
    $"every global bundle argument is explicitly typed (got {TypedSummary(sparseBundleBindings)})");

var sparseBundleCommand = await BuildAsync(EfGlobalBundleSnapshotWriter.UpsertSql, sparseBundleBindings);
Check(
    sparseBundleCommand.Failure is null,
    $"the global bundle upsert builds when every optional provider field is absent (got {Describe(sparseBundleCommand)})");
CheckFingerprint(
    "itad_global|itad-bundle-sparse|Bundle sin datos opcionales|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|2026-10-19T10:13:15Z",
    "the global bundle upsert binds 8 typed nulls and the observed timestamp in place",
    sparseBundleCommand);
Check(
    sparseBundleCommand.Parameters.Count == 12 && !sparseBundleCommand.Text.Contains('{', StringComparison.Ordinal),
    "the global bundle upsert substitutes all 12 placeholders");
Check(
    sparseBundleCommand.Parameters.Where(parameter => parameter.Value is DBNull).All(parameter => parameter.ExplicitStoreType),
    "every null global bundle parameter carries an explicit store type");

// Preserve: a fully populated bundle keeps its values, its affiliate link verbatim and its tiers JSON.
var richBundle = new ItadBundle(
    "itad-bundle-rich",
    "Bundle completo",
    "https://isthereanydeal.com/bundle/complete",
    "steam",
    "Steam",
    "Dos juegos por un precio",
    observedAt.AddDays(-2),
    observedAt.AddDays(5),
    "https://itad.link/affiliate/complete?tag=dealext",
    [new ItadBundleTier(999, "MXN", Addon: false, ItemsComplete: true, [new ItadBundleItem("itad-1", "Juego", "game", 499, "MXN")])],
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "itad-1" },
    AttributionComplete: true);

var richBundleBindings = EfGlobalBundleSnapshotWriter.BuildUpsertBindings(richBundle, observedAt);
var richBundleCommand = await BuildAsync(EfGlobalBundleSnapshotWriter.UpsertSql, richBundleBindings);
Check(
    richBundleCommand.Failure is null,
    $"the global bundle upsert builds for a fully populated bundle (got {Describe(richBundleCommand)})");
CheckFingerprint(
    "itad_global|itad-bundle-rich|Bundle completo|steam|Steam|https://isthereanydeal.com/bundle/complete|" +
    "https://itad.link/affiliate/complete?tag=dealext|Dos juegos por un precio|2026-10-17T10:13:15Z|" +
    "[{\"priceMinor\":999,\"currency\":\"MXN\",\"addon\":false,\"itemsComplete\":true,\"games\":[{\"title\":\"Juego\",\"type\":\"game\",\"priceMinor\":499,\"priceCurrency\":\"MXN\",\"itadGameId\":\"itad-1\",\"url\":null}],\"selectionCount\":null}]|" +
    "2026-10-24T10:13:15Z|2026-10-19T10:13:15Z",
    "a fully populated global bundle keeps its stored values in place",
    richBundleCommand);

// --- B. gg.deals bundle snapshot -------------------------------------------------

// The provider published no article link and no end date: page URL, deal URL, details, tiers and
// expires_at are all absent, which is the null shape that failed here too.
var sparseGgBundle = new GgDealsBundle(
    "gg1:sparse",
    "Bundle gg.deals sin enlace",
    Url: null,
    DateFrom: observedAt.AddDays(-1),
    DateTo: null,
    Tiers: []);

var sparseGgBindings = EfGgDealsBundleSnapshotWriter.BuildUpsertBindings(sparseGgBundle, observedAt);
Check(
    ExplicitlyTyped(sparseGgBindings),
    $"every gg.deals bundle argument is explicitly typed (got {TypedSummary(sparseGgBindings)})");

var sparseGgCommand = await BuildAsync(EfGgDealsBundleSnapshotWriter.UpsertSql, sparseGgBindings);
Check(
    sparseGgCommand.Failure is null,
    $"the gg.deals bundle upsert builds when the article has no link and no end date (got {Describe(sparseGgCommand)})");
CheckFingerprint(
    "ggdeals|gg1:sparse|Bundle gg.deals sin enlace|gg.deals|GG.deals|NULL|NULL|NULL|2026-10-18T10:13:15Z|NULL|NULL|2026-10-19T10:13:15Z",
    "the gg.deals bundle upsert binds 5 typed nulls and keeps the fixed shop identity",
    sparseGgCommand);

// Preserve: gg.deals publishes no affiliate redirector, so deal_url is null even for a complete article.
var richGgBundle = new GgDealsBundle(
    "gg1:rich",
    "Bundle gg.deals completo",
    Url: "https://gg.deals/bundles/completo/",
    DateFrom: observedAt.AddDays(-1),
    DateTo: observedAt.AddDays(4),
    Tiers: [new GgDealsBundleTier(4999, "MXN", GamesCount: 1, Games: [new GgDealsBundleItem("Juego", "https://gg.deals/game/juego/")])]);

var richGgBindings = EfGgDealsBundleSnapshotWriter.BuildUpsertBindings(richGgBundle, observedAt);
var richGgCommand = await BuildAsync(EfGgDealsBundleSnapshotWriter.UpsertSql, richGgBindings);
Check(
    richGgCommand.Failure is null,
    $"the gg.deals bundle upsert builds for a complete article (got {Describe(richGgCommand)})");
CheckFingerprint(
    "ggdeals|gg1:rich|Bundle gg.deals completo|gg.deals|GG.deals|https://gg.deals/bundles/completo/|NULL|NULL|" +
    "2026-10-18T10:13:15Z|" +
    "[{\"priceMinor\":4999,\"currency\":\"MXN\",\"addon\":false,\"itemsComplete\":true,\"games\":[{\"title\":\"Juego\",\"type\":\"game\",\"priceMinor\":null,\"priceCurrency\":null,\"itadGameId\":null,\"url\":\"https://gg.deals/game/juego/\"}],\"selectionCount\":1}]|" +
    "2026-10-23T10:13:15Z|2026-10-19T10:13:15Z",
    "a complete gg.deals bundle keeps its page link, tiers and dates and still stores no deal_url",
    richGgCommand);

// --- C. Global deals snapshot ----------------------------------------------------

// A deal the provider quoted in a currency with no usable rate: no slug, no game type, no regular price,
// no discount, no history low, no FX figures, no deal URL and no expiry. Thirteen absent fields.
var sparseDeal = new ItadGlobalDeal(
    "deal-sparse",
    "itad-game-sparse",
    "Juego sin datos opcionales",
    GameSlug: null,
    GameType: null,
    GameMature: false,
    ShopId: "steam",
    ShopName: "Steam",
    IsOfficial: true,
    Currency: "EUR",
    CurrentPriceMinor: 1999,
    RegularPriceMinor: null,
    DiscountPercent: null,
    HistoryLowMinor: null,
    HistoryLowCurrency: null,
    DealUrl: null,
    ExpiresAt: null);

var sparseProjection = GlobalDealCurrency.Project(sparseDeal.Currency, sparseDeal.CurrentPriceMinor, usdToMxn: null);
var sparseDealBindings = EfGlobalDealSnapshotWriter.BuildUpsertBindings(sparseDeal, sparseProjection, observedAt);
Check(
    ExplicitlyTyped(sparseDealBindings),
    $"every global deal argument is explicitly typed (got {TypedSummary(sparseDealBindings)})");

var sparseDealCommand = await BuildAsync(EfGlobalDealSnapshotWriter.UpsertSql, sparseDealBindings);
Check(
    sparseDealCommand.Failure is null,
    $"the global deals upsert builds when the projection claims no MXN price (got {Describe(sparseDealCommand)})");
CheckFingerprint(
    "itad_global_deals|deal-sparse|itad-game-sparse|Juego sin datos opcionales|NULL|NULL|False|steam|Steam|True|EUR|1999|" +
    "NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|unconverted|NULL|NULL|2026-10-19T10:13:15Z",
    "the global deals upsert binds 13 typed nulls and keeps the unconverted classification",
    sparseDealCommand);
Check(
    sparseDealCommand.Parameters.Count == 24 && !sparseDealCommand.Text.Contains('{', StringComparison.Ordinal),
    "the global deals upsert substitutes all 24 placeholders");

// Preserve: an exact MXN quote stays regional, keeps its amount and stores no FX figure.
var richDeal = new ItadGlobalDeal(
    "deal-rich",
    "itad-game-rich",
    "Juego regional",
    "juego-regional",
    "game",
    GameMature: false,
    ShopId: "steam",
    ShopName: "Steam",
    IsOfficial: true,
    Currency: "MXN",
    CurrentPriceMinor: 19900,
    RegularPriceMinor: 29900,
    DiscountPercent: 33,
    HistoryLowMinor: 14900,
    HistoryLowCurrency: "MXN",
    DealUrl: "https://itad.link/deal/deal-rich?tag=dealext",
    ExpiresAt: observedAt.AddDays(3));

var richProjection = GlobalDealCurrency.Project(richDeal.Currency, richDeal.CurrentPriceMinor, usdToMxn: null);
var richDealCommand = await BuildAsync(
    EfGlobalDealSnapshotWriter.UpsertSql,
    EfGlobalDealSnapshotWriter.BuildUpsertBindings(richDeal, richProjection, observedAt));
Check(
    richDealCommand.Failure is null,
    $"the global deals upsert builds for a complete regional deal (got {Describe(richDealCommand)})");
CheckFingerprint(
    "itad_global_deals|deal-rich|itad-game-rich|Juego regional|juego-regional|game|False|steam|Steam|True|MXN|19900|" +
    "29900|33|14900|MXN|19900|NULL|NULL|NULL|regional|https://itad.link/deal/deal-rich?tag=dealext|" +
    "2026-10-22T10:13:15Z|2026-10-19T10:13:15Z",
    "a complete regional global deal keeps its amounts, prices and affiliate link",
    richDealCommand);

// --- D. Merge audit insert (nullable merged_by) ----------------------------------

// The merge log's actor name is nullable: a merge started by a principal without a display name must
// still be bindable. This statement runs through SqlQueryRaw, and that path does not raise the type
// mapping error at build time (measured, unlike the ExecuteSqlRaw path above), so the rule is asserted on
// the binding itself: a typed null, never a bare DBNull that would leave the type to server-side inference.
var anonymousAuditBindings = GameMergeService.BuildMergeAuditBindings(11, 22, "[]", actorName: null, actorUserId: 7);
Check(
    ExplicitlyTyped(anonymousAuditBindings),
    $"the merge audit insert binds a missing actor as a typed null (got {TypedSummary(anonymousAuditBindings)})");
Check(
    anonymousAuditBindings[3] is NpgsqlParameter { NpgsqlDbType: NpgsqlDbType.Text, Value: DBNull },
    "the merge audit merged_by slot is a text parameter carrying DBNull");

var anonymousAuditCommand = await BuildQueryAsync(GameMergeService.MergeAuditInsertSql, anonymousAuditBindings);
Check(
    anonymousAuditCommand.Failure is null,
    $"the merge audit insert builds when the actor has no display name (got {Describe(anonymousAuditCommand)})");
CheckFingerprint(
    "11|22|[]|NULL|7",
    "the merge audit insert binds survivor, absorbed, snapshot, null actor and user id in place",
    anonymousAuditCommand);

// Preserve: a named actor is still trimmed and capped at 100 characters, as the column requires.
var longActorName = "  " + new string('a', 120) + "  ";
var namedAuditCommand = await BuildQueryAsync(
    GameMergeService.MergeAuditInsertSql,
    GameMergeService.BuildMergeAuditBindings(11, 22, "[]", longActorName, actorUserId: 7));
CheckFingerprint(
    "11|22|[]|" + new string('a', 100) + "|7",
    "a named actor is trimmed and capped at 100 characters",
    namedAuditCommand);

// --- Result ----------------------------------------------------------------------

if (failures.Count > 0)
{
    Console.WriteLine($"SqlParameters.Checks: {failures.Count} check(s) failed");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  FAIL {failure}");
    }

    return 1;
}

Console.WriteLine("SqlParameters.Checks: all checks passed");
return 0;

internal sealed record BuiltParameter(string Name, object? Value, string StoreType, bool ExplicitStoreType);

internal sealed record BuiltCommand(
    string Text,
    IReadOnlyList<BuiltParameter> Parameters,
    Exception? Failure);

internal sealed class CaptureContext(DbContextOptions<CaptureContext> options) : DbContext(options);

internal sealed class MergeAuditRow
{
    public long GameMergeId { get; set; }
}

/// <summary>
/// Stands in for the PostgreSQL connection. EF creates its command through this connection and never
/// opens it, so the exact command it would have sent can be inspected with no server and no network.
/// </summary>
internal sealed class CaptureConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    public CaptureCommand? Command { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "capture";

    public override string Database => "capture";

    public override string DataSource => "capture";

    public override string ServerVersion => "capture";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => Command = new CaptureCommand();
}

/// <summary>Records the parameters EF bound before answering "no rows".</summary>
internal sealed class CaptureCommand : DbCommand
{
    private readonly CaptureParameters _parameters = new();

    // EF clears the parameter list when it disposes the command, so the snapshot is taken at execution.
    private IReadOnlyList<BuiltParameter>? _captured;

    public string Text => CommandText;

    public IReadOnlyList<BuiltParameter> Capture() => _captured ?? Snapshot();

    private IReadOnlyList<BuiltParameter> Snapshot() =>
        _captured ??= _parameters.Items
            .Select(parameter => parameter is NpgsqlParameter npgsql
                ? new BuiltParameter(
                    npgsql.ParameterName,
                    npgsql.Value,
                    npgsql.NpgsqlDbType.ToString(),
                    npgsql.NpgsqlDbType != NpgsqlDbType.Unknown)
                : new BuiltParameter(parameter.ParameterName, parameter.Value, parameter.DbType.ToString(), false))
            .ToList();

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        Snapshot();
        return 0;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        Snapshot();
        return Task.FromResult(0);
    }

    public override object? ExecuteScalar() => null;

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new NpgsqlParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Snapshot();
        return new EmptyReader();
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        Snapshot();
        return Task.FromResult<DbDataReader>(new EmptyReader());
    }
}

internal sealed class CaptureParameters : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public List<DbParameter> Items => _items;

    public override int Count => _items.Count;

    public override object SyncRoot => _items;

    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((DbParameter)value);

    public override bool Contains(string value) => _items.Any(parameter => parameter.ParameterName == value);

    public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);

    public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(parameter => parameter.ParameterName == parameterName);

    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter? value) => _items[index] = value!;

    protected override void SetParameter(string parameterName, DbParameter? value) =>
        _items[IndexOf(parameterName)] = value!;
}

/// <summary>An empty result set: the statement is never executed, only built.</summary>
internal sealed class EmptyReader : DbDataReader
{
    public override int Depth => 0;

    public override int FieldCount => 0;

    public override bool HasRows => false;

    public override bool IsClosed => true;

    public override int RecordsAffected => 0;

    public override object this[int ordinal] => throw new IndexOutOfRangeException();

    public override object this[string name] => throw new IndexOutOfRangeException();

    public override bool GetBoolean(int ordinal) => throw new IndexOutOfRangeException();

    public override byte GetByte(int ordinal) => throw new IndexOutOfRangeException();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;

    public override char GetChar(int ordinal) => throw new IndexOutOfRangeException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;

    public override string GetDataTypeName(int ordinal) => "integer";

    public override DateTime GetDateTime(int ordinal) => throw new IndexOutOfRangeException();

    public override decimal GetDecimal(int ordinal) => throw new IndexOutOfRangeException();

    public override double GetDouble(int ordinal) => throw new IndexOutOfRangeException();

    public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

    public override Type GetFieldType(int ordinal) => typeof(int);

    public override float GetFloat(int ordinal) => throw new IndexOutOfRangeException();

    public override Guid GetGuid(int ordinal) => throw new IndexOutOfRangeException();

    public override short GetInt16(int ordinal) => throw new IndexOutOfRangeException();

    public override int GetInt32(int ordinal) => throw new IndexOutOfRangeException();

    public override long GetInt64(int ordinal) => throw new IndexOutOfRangeException();

    public override string GetName(int ordinal) => "GameMergeId";

    public override int GetOrdinal(string name) => 0;

    public override string GetString(int ordinal) => throw new IndexOutOfRangeException();

    public override object GetValue(int ordinal) => throw new IndexOutOfRangeException();

    public override int GetValues(object[] values) => 0;

    public override bool IsDBNull(int ordinal) => true;

    public override bool NextResult() => false;

    public override bool Read() => false;

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
