using System.Globalization;
using System.Text.RegularExpressions;

namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// The Heroic library envelope and the rules its gate applies before anything is written
/// (<c>odd/tasks/heroic-library-export.md</c>, S3/S4/S5). Heroic is the manual path: a local script reads
/// the Epic/GOG caches and the person uploads the JSON by hand, so this contract replaces Playnite's plugin
/// ids with the two stores Heroic can actually export and never carries ownership, dates or paths.
///
/// <para>
/// The envelope is frozen as <c>{format:"dealext-library", version:1, source:"heroic", exportedAt:ISO UTC,
/// games:[{store, storeGameId, title, isInstalled}]}</c>. Everything else — the state, the identity, the
/// catalog link — is decided server-side by the same loop the Playnite import uses, so a Heroic upload can
/// never claim something a Playnite upload could not.
/// </para>
///
/// <para>
/// <see cref="Validate"/> is pure and DB-free on purpose: every rule that decides whether a payload is
/// accepted lives here, so <c>checks/HeroicLibraryExport.Checks</c> can pin the whole decision surface
/// without a PostgreSQL. The rules run to completion before the controller opens its transaction, because
/// one bad row discovered mid-loop would either lose the accepted rows or commit half an import.
/// </para>
/// </summary>
public static class HeroicLibraryExportLimits
{
    /// <summary>Literal of <c>format</c>. Frozen: a different value is a different product's export.</summary>
    public const string Format = "dealext-library";

    /// <summary>Literal of <c>version</c>. A bump means a new shape, so an old server must refuse it.</summary>
    public const int Version = 1;

    /// <summary>Literal of <c>source</c>. Heroic is not Playnite and its rows are not plugin-keyed.</summary>
    public const string Source = "heroic";

    /// <summary>
    /// Hard cap per payload, shared with the Playnite gate: <see cref="HeroicLibraryExportLimits.MaxEntries"/> is
    /// the single source of the number the controller enforces on both endpoints. The Heroic input is an
    /// addition, never a wider door: a payload over the cap is refused before any row is read.
    /// </summary>
    public const int MaxEntries = 10_000;

    /// <summary><c>user_library.title</c> is <c>VARCHAR(256)</c>; mirrors <see cref="LibraryImportLimits"/>.</summary>
    public const int MaxTitleLength = 256;

    /// <summary><c>user_library.store_game_id</c> is <c>VARCHAR(64)</c>; mirrors <see cref="LibraryImportLimits"/>.</summary>
    public const int MaxStoreGameIdLength = 64;

    /// <summary>Epic store key, as written to <c>user_library.store</c>.</summary>
    public const string Epic = StoreKeys.Epic;

    /// <summary>GOG store key, as written to <c>user_library.store</c>.</summary>
    public const string Gog = StoreKeys.Gog;

    /// <summary>
    /// The only two stores the envelope may name, in contract order. Steam stays on the direct profile read
    /// and Xbox/Amazon/Ubisoft/Humble/Battle.net have no Heroic cache: naming one of them is a malformed
    /// export, not a row to skip, because a skipped row is a game silently missing from the library.
    /// </summary>
    public static readonly IReadOnlyList<string> Stores = [Epic, Gog];

    /// <summary>True when <paramref name="store"/> is <see cref="Epic"/> or <see cref="Gog"/> (ordinal).</summary>
    public static bool IsSupportedStore(string? store) => store is Epic or Gog;
}

/// <summary>
/// One <c>games</c> element as the API binds it. Every field is nullable so a missing one is reported as
/// "required" by <see cref="HeroicLibraryExportRules"/> instead of being silently defaulted: a missing
/// <c>isInstalled</c> must never be read as false.
/// </summary>
public sealed record HeroicLibraryEntryInput(string? Store, string? StoreGameId, string? Title, bool? IsInstalled);

/// <summary>
/// One accepted row, normalized and ready for the shared import loop. <see cref="State"/> is always
/// <see cref="LibraryStates.Owned"/>: the envelope has no ownership field, and Heroic only lists entries of an
/// owned library, so the server fills the state from the user's own claim instead of letting the file assert
/// it. That is a user assertion, not a verified ownership proof — the same thing a Playnite import says.
/// </summary>
public sealed record HeroicLibraryRow(string Store, string StoreGameId, string Title, bool IsInstalled, string State);

/// <summary>
/// Result of the gate: the accepted rows and how many were accepted per store (the same breakdown the
/// Playnite report shows). Duplicates inside one payload are not removed here; the shared loop keeps the
/// first occurrence, so the counts are counts of accepted entries, like Playnite's.
/// </summary>
public sealed record HeroicLibraryValidationResult(
    IReadOnlyList<HeroicLibraryRow> Rows,
    IReadOnlyDictionary<string, int> ByStore);

/// <summary>
/// The Heroic gate. Every failure is an <see cref="ArgumentException"/>, which the API maps to 400, and every
/// message names the wire field so a hand-edited file knows what to fix. Nothing here reads the database,
/// the clock or the network.
/// </summary>
public static class HeroicLibraryExportRules
{
    /// <summary>
    /// ISO 8601 with an explicit zero offset. The envelope promises UTC, so a local offset, a missing zone or
    /// a bare date is refused instead of being assumed: <c>DateTimeOffset.TryParse</c> is deliberately not used
    /// here, because with <c>AssumeUniversal</c> it accepts all three and produces a plausible UTC instant from
    /// a file that never said which zone it meant. Kept byte-for-byte equal to the regular expression in
    /// <c>Deals.Web/lib/contracts/heroic-library-export.ts</c>, so the browser gate and the server gate cannot
    /// disagree about what a valid <c>exportedAt</c> is.
    /// </summary>
    private static readonly Regex IsoUtcTimestamp = new(
        @"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?(?:\.(\d{1,3}))?(Z|\+00:00)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Store ids are identity, so they are never silently rewritten: a value with surrounding whitespace or a
    /// control character is rejected, because trimming it would write an identity the file never contained and
    /// a NUL byte cannot even be stored by PostgreSQL. Internal characters are left alone.
    /// </summary>
    private static readonly Regex ControlCharacter = new(@"[\u0000-\u001F\u007F]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates the whole envelope and returns the rows the shared import loop will upsert. Throws on the
    /// first violation: the caller opens its transaction only after this returns, so a throw means nothing
    /// was written.
    /// </summary>
    public static HeroicLibraryValidationResult Validate(
        string? format,
        int? version,
        string? source,
        string? exportedAt,
        IReadOnlyList<HeroicLibraryEntryInput?>? games)
    {
        // The envelope is frozen and compared ordinally: a near miss is a different schema, and guessing
        // which one was meant is how an unsupported file gets half-imported.
        if (!string.Equals(format, HeroicLibraryExportLimits.Format, StringComparison.Ordinal))
        {
            throw new ArgumentException($"format debe ser \"{HeroicLibraryExportLimits.Format}\"", nameof(format));
        }

        if (version != HeroicLibraryExportLimits.Version)
        {
            throw new ArgumentException($"version debe ser {HeroicLibraryExportLimits.Version}", nameof(version));
        }

        if (!string.Equals(source, HeroicLibraryExportLimits.Source, StringComparison.Ordinal))
        {
            throw new ArgumentException($"source debe ser \"{HeroicLibraryExportLimits.Source}\"", nameof(source));
        }

        // A bad timestamp rejects the file before any row is read. Nothing stores it today: the row's own
        // date is absent by contract (`added_at` is kept from whatever is already stored).
        _ = ParseExportedAt(exportedAt);

        if (games is null || games.Count == 0)
        {
            throw new ArgumentException("games debe ser un arreglo con al menos un juego", nameof(games));
        }

        if (games.Count > HeroicLibraryExportLimits.MaxEntries)
        {
            throw new ArgumentException(
                $"games no puede exceder {HeroicLibraryExportLimits.MaxEntries} juegos",
                nameof(games));
        }

        var rows = new List<HeroicLibraryRow>(games.Count);
        var byStore = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in games)
        {
            if (entry is null)
            {
                throw new ArgumentException("games no puede contener entradas nulas", nameof(games));
            }

            var store = NormalizeStore(entry.Store);
            var storeGameId = RequireStoreGameId(entry.StoreGameId);
            var title = RequireText(entry.Title, "title", HeroicLibraryExportLimits.MaxTitleLength);
            var isInstalled = entry.IsInstalled
                ?? throw new ArgumentException("isInstalled es obligatorio en cada juego", "isInstalled");

            // GOG identifies a game by a numeric product id. A non-numeric one would create a library row
            // that no GOG lookup can ever resolve, so it is refused here instead of stored as a dead row.
            if (store == HeroicLibraryExportLimits.Gog && !IsNumericId(storeGameId))
            {
                throw new ArgumentException($"storeGameId debe ser numérico para gog (llegó \"{storeGameId}\")", "storeGameId");
            }

            rows.Add(new HeroicLibraryRow(store, storeGameId, title, isInstalled, LibraryStates.Owned));
            byStore[store] = byStore.GetValueOrDefault(store) + 1;
        }

        return new HeroicLibraryValidationResult(rows, byStore);
    }

    /// <summary>
    /// Parses <c>exportedAt</c>, which is required and must be ISO 8601 in UTC with an explicit zero offset. A
    /// local offset, a missing zone or a bare date is refused rather than converted: the envelope promises UTC,
    /// so anything else means the exporter and the server disagree about what the field means.
    /// </summary>
    public static DateTime ParseExportedAt(string? exportedAt)
    {
        if (string.IsNullOrWhiteSpace(exportedAt))
        {
            throw new ArgumentException("exportedAt es obligatorio", "exportedAt");
        }

        var match = IsoUtcTimestamp.Match(exportedAt.Trim());
        if (!match.Success || !TryBuildUtc(match, out var utc))
        {
            throw new ArgumentException(
                "exportedAt debe ser una marca ISO 8601 en UTC, por ejemplo 2026-10-02T18:04:05Z",
                "exportedAt");
        }

        return utc;
    }

    /// <summary>
    /// Rejects anything that is not exactly an id: blank, longer than the column, surrounded by whitespace or
    /// carrying a control character. Unlike <c>title</c> (display text, trimmed), this value is a key that
    /// <c>game_external_ids</c> stores, so it is validated as written.
    /// </summary>
    private static string RequireStoreGameId(string? value)
    {
        var text = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("storeGameId es obligatorio", "storeGameId");
        }

        if (!string.Equals(text, text.Trim(), StringComparison.Ordinal) || ControlCharacter.IsMatch(text))
        {
            throw new ArgumentException(
                "storeGameId no puede llevar espacios al inicio o al final ni caracteres de control",
                "storeGameId");
        }

        return text.Length > HeroicLibraryExportLimits.MaxStoreGameIdLength
            ? throw new ArgumentException(
                $"storeGameId no puede exceder {HeroicLibraryExportLimits.MaxStoreGameIdLength} caracteres",
                "storeGameId")
            : text;
    }

    /// <summary>
    /// Builds the instant from the regex groups and lets <see cref="DateTime"/> validate the calendar: an
    /// impossible day such as 2026-02-30 throws and becomes a 400 instead of rolling over into March.
    /// </summary>
    private static bool TryBuildUtc(Match match, out DateTime utc)
    {
        utc = default;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            return false;
        }

        var second = 0;
        if (match.Groups[6].Success &&
            !int.TryParse(match.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out second))
        {
            return false;
        }

        var millisecond = 0;
        if (match.Groups[7].Success &&
            !int.TryParse(
                match.Groups[7].Value.PadRight(3, '0'),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out millisecond))
        {
            return false;
        }

        try
        {
            utc = new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Trims and lowercases the store so <c> GOG </c> cannot become a second key in <c>user_library.store</c>,
    /// then requires one of the two supported stores. Same normalization the Playnite gate applies to
    /// <c>Source</c>: the written value is canonical, never the raw text.
    /// </summary>
    private static string NormalizeStore(string? store)
    {
        var text = store?.Trim().ToLowerInvariant();
        if (!HeroicLibraryExportLimits.IsSupportedStore(text))
        {
            throw new ArgumentException(
                $"store debe ser {string.Join(" o ", HeroicLibraryExportLimits.Stores)} (llegó \"{store}\")",
                "store");
        }

        // Reaching here means IsSupportedStore matched one of the two constants, so the cast cannot fail.
        return text!;
    }

    /// <summary>
    /// Non-blank and within the column width. Rejecting here costs a 400 for the offending file; not
    /// rejecting costs the whole import, because the 22001 PostgreSQL raises aborts the transaction every
    /// row shares (same reasoning as <see cref="LibraryImportLimits"/>).
    /// </summary>
    private static string RequireText(string? value, string field, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException($"{field} es obligatorio", field);
        }

        return text.Length > maxLength
            ? throw new ArgumentException($"{field} no puede exceder {maxLength} caracteres", field)
            : text;
    }

    /// <summary>ASCII digits only: <c>char.IsDigit</c> would also accept non-ASCII numerals.</summary>
    private static bool IsNumericId(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}

/// <summary>
/// The <c>user_library.added_at</c> decision shared by both import gates. The Playnite export always carries
/// <c>Added</c> (its parser rejects a blank one), so a Playnite reimport keeps writing it unchanged. The
/// Heroic envelope has no date at all, so an absent value must keep whatever is already stored: nulling it
/// would erase the only date the row ever had, on every upload.
/// </summary>
public static class LibraryImportDates
{
    /// <summary>The incoming date when the payload supplies one, otherwise the stored one.</summary>
    public static DateTime? Merge(DateTime? existing, DateTime? incoming) => incoming ?? existing;
}
