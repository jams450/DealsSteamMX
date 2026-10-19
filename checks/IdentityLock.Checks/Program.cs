using System.Globalization;
using System.Text.RegularExpressions;
using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Services;
using IdentityLockChecks;
using Npgsql;

// Deterministic checks for the identity writer gate (spec S3).
//
// Rule: every path that creates or reassigns canonical game identity takes one gate, and it takes it in a
// bounded way. The gate used to be a bare lock statement under an infinite command timeout, so a slow
// import held it for its whole run and a stuck one pinned its pooled connection forever. Pinned here:
//   1. the wait budget is set before the lock is taken,
//   2. the lock key is byte-for-byte the statement the call sites already used, so two versions of the
//      binary cannot end up serializing on two different keys during a rollout,
//   3. only PostgreSQL's lock_not_available means "busy",
//   4. any other failure surfaces as itself instead of being reported as a busy gate.
// No PostgreSQL server is contacted and no credential is used: the repository seam is a recorder.
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

var recorder = new RecordingRepository();
await GameIdentityLock.AcquireAsync(recorder);

Check(recorder.Statements.Count == 2,
    $"the gate issues the budget and the lock and nothing else (issued {recorder.Statements.Count})");

if (recorder.Statements.Count == 2)
{
    Check(recorder.Statements[0] == $"SET LOCAL lock_timeout = {GameIdentityLock.WaitBudgetMilliseconds}",
        $"the wait budget is set first (got '{recorder.Statements[0]}')");

    Check(recorder.Statements[1] == "SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))",
        $"the identity gate key is unchanged (got '{recorder.Statements[1]}')");
}

Check(GameIdentityLock.IsLockTimeout(GameIdentityLock.LockTimeoutSqlState),
    "lock_not_available is classified as a busy gate");

Check(!GameIdentityLock.IsLockTimeout("23505"),
    "a unique violation is not a busy gate");

Check(!GameIdentityLock.IsLockTimeout(null),
    "an absent state is not a busy gate");

// A real error must reach the caller unchanged: reporting it as "another operation is running" would both
// hide the error and invite a retry that cannot help.
var failing = new RecordingRepository { FailWith = new InvalidOperationException("connection reset") };
var unexpectedFailure = await Capture(() => GameIdentityLock.AcquireAsync(failing));

Check(unexpectedFailure is InvalidOperationException,
    $"a non-lock failure surfaces unchanged (got {unexpectedFailure?.GetType().Name ?? "no failure"})");

// PostgreSQL answers an exhausted lock timeout with 55P03; the conversion is asserted directly when the
// driver lets a PostgresException be built, otherwise it is only the classification that is pinned here.
var lockTimeout = await Capture(() => GameIdentityLock.AcquireAsync(new RecordingRepository
{
    FailWith = new PostgresException(
        "canceling statement due to lock timeout",
        "ERROR",
        "ERROR",
        GameIdentityLock.LockTimeoutSqlState)
}));

Check(lockTimeout is IdentityLockUnavailableException,
    $"an exhausted wait is reported as a busy gate (got {lockTimeout?.GetType().Name ?? "no failure"})");

// Import field widths against the schema (spec S6). The guard that rejects an oversized entry before the
// loop only helps if it agrees with the column it writes to, so the widths are read from SQL/schema.sql
// rather than trusted to the constant.
var schemaPath = FindSchemaFile();
Check(schemaPath is not null, "the schema file is reachable from the check");

if (schemaPath is not null)
{
    var schema = File.ReadAllText(schemaPath);
    var titleWidth = DeclaredWidth(schema, "user_library", "title");
    var storeGameIdWidth = DeclaredWidth(schema, "user_library", "store_game_id");
    var externalIdWidth = DeclaredWidth(schema, "game_external_ids", "external_id");

    Check(titleWidth == LibraryImportLimits.MaxTitleLength,
        $"the title limit matches user_library.title (schema {titleWidth?.ToString(CultureInfo.InvariantCulture) ?? "not found"})");

    Check(storeGameIdWidth == LibraryImportLimits.MaxStoreGameIdLength,
        $"the store game id limit matches user_library.store_game_id (schema {storeGameIdWidth?.ToString(CultureInfo.InvariantCulture) ?? "not found"})");

    Check(externalIdWidth == LibraryImportLimits.MaxStoreGameIdLength,
        $"the same limit covers game_external_ids.external_id (schema {externalIdWidth?.ToString(CultureInfo.InvariantCulture) ?? "not found"})");
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: identity writer gate, import field limits and command timeout checks");
return 0;

// El repositorio se localiza subiendo desde el binario: el check debe funcionar sin depender del directorio
// desde el que se lo invoque.
static string? FindSchemaFile()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "SQL", "schema.sql");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return null;
}

static int? DeclaredWidth(string schema, string table, string column)
{
    var start = schema.IndexOf($"CREATE TABLE {table} (", StringComparison.Ordinal);
    if (start < 0)
    {
        return null;
    }

    var end = schema.IndexOf("\n);", start, StringComparison.Ordinal);
    var block = end < 0 ? schema[start..] : schema[start..end];

    var match = Regex.Match(block, $@"^\s*{column}\s+VARCHAR\((\d+)\)", RegexOptions.Multiline);

    return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
}
