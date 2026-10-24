using System.Globalization;

namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// The five acquisition kinds the wishlist dialog offers (spec S1). They are a closed vocabulary on
/// purpose: the value is stored in <c>user_library.acquisition_type</c> and mirrored by a CHECK, so an
/// unknown value must be a 400 at the door instead of a row nobody can classify later.
///
/// <para>
/// <see cref="Subscription"/> is not a flavour of ownership: an acquired subscription writes
/// <c>state = 'subscription'</c>, while the other four write <c>state = 'owned'</c> (see
/// <see cref="WishlistAcquisitionRules.StateFor"/>). Nothing else in the row depends on the type.
/// </para>
/// </summary>
public static class WishlistAcquisitionTypes
{
    public const string Purchase = "purchase";
    public const string Gift = "gift";
    public const string Free = "free";
    public const string Bundle = "bundle";
    public const string Subscription = "subscription";

    public static IReadOnlyList<string> All { get; } = [Purchase, Gift, Free, Bundle, Subscription];

    /// <summary>True for one of the five contract values (ordinal, already normalized).</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Normalized vocabulary value: trimmed and lowercased, or <c>null</c> when it is not one of the five.
    /// Callers turn null into a 400 instead of writing an unvalidated type.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        return IsKnown(text) ? text : null;
    }
}

/// <summary>
/// Classification of the shop recorded in <c>user_library.acquired_store</c> (spec S6/L14): whether that
/// shop is the official storefront (<c>official</c>), a keyshop / grey-market reseller (<c>keyshop</c>), or
/// not classified (<c>unknown</c>). It is a separate fact from the shop itself and it is <b>never</b>
/// inferred from a price, an offer or a provider: the person selects it, and an unclassified acquisition is
/// stored as <c>NULL</c> instead of a guess.
///
/// <para>
/// The value lives in <c>user_library.acquired_store_type</c> and is mirrored by a CHECK, so an unknown value
/// must be a 400 at the door instead of a row nobody can classify later. The list is closed on purpose:
/// every classification the UI can offer has to be here, or the request is rejected.
/// </para>
/// </summary>
public static class WishlistAcquiredStoreTypes
{
    public const string Official = "official";
    public const string Keyshop = "keyshop";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [Official, Keyshop, Unknown];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string? Normalize(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        return IsKnown(text) ? text : null;
    }
}

/// <summary>
/// Where the acquisition's offer was discovered (spec S6/L14): ITAD, gg.deals, the shop itself
/// (<c>direct</c>), or some other source. Like the shop classification this is a manually registered fact
/// about how the person found the copy they bought — never derived from the prices the product stores — and
/// it is optional, so an acquisition that does not record one keeps <c>NULL</c>.
///
/// <para>
/// The value lives in <c>user_library.acquisition_provider</c> and is mirrored by a CHECK; the vocabulary is
/// closed for the same reason as <see cref="WishlistAcquiredStoreTypes"/>.
/// </para>
/// </summary>
public static class WishlistAcquisitionProviders
{
    public const string Itad = "itad";
    public const string GgDeals = "ggdeals";
    public const string Direct = "direct";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Itad, GgDeals, Direct, Other];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string? Normalize(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        return IsKnown(text) ? text : null;
    }
}

/// <summary>Wire-visible outcomes of one acquisition (L4 contract).</summary>
public static class WishlistAcquisitionOutcomes
{
    /// <summary>A new owned/subscription library row was created from the wished game.</summary>
    public const string Created = "created";

    /// <summary>The chosen platform already had an owned/subscription row for this game: nothing was created.</summary>
    public const string AlreadyOwned = "already_owned";
}

/// <summary>
/// Raw acquisition payload, exactly as the endpoint receives it. Every text field is nullable because the
/// client is untrusted: <see cref="WishlistAcquisitionRules.Validate"/> is the only thing that turns this
/// into a <see cref="WishlistAcquisitionInput"/>.
///
/// <para>
/// <see cref="AcquiredStoreType"/> and <see cref="AcquisitionProvider"/> were added after the first version
/// of the endpoint (S6/L14) and are <b>optional parameters</b> on purpose: a caller that still sends the
/// five-field payload keeps compiling, and an absent or null value means "not registered" rather than an
/// error. Only a non-null value outside the vocabulary is a 400.
/// </para>
/// </summary>
public sealed record WishlistAcquisitionRequest(
    string? Platform,
    string? AcquiredStore,
    string? AcquisitionType,
    DateOnly? AcquiredAt,
    bool RemoveFromWishlist,
    string? AcquiredStoreType = null,
    string? AcquisitionProvider = null);

/// <summary>
/// Validated acquisition payload: normalized platform, store, type, date and the two optional source facts
/// (<c>AcquiredStoreType</c>, <c>AcquisitionProvider</c>), which stay null when the caller did not send them.
/// </summary>
public sealed record WishlistAcquisitionInput(
    string Platform,
    string AcquiredStore,
    string AcquisitionType,
    DateOnly? AcquiredAt,
    bool RemoveFromWishlist,
    string? AcquiredStoreType = null,
    string? AcquisitionProvider = null);

/// <summary>
/// Result of one acquisition. <see cref="Dismissed"/> is true whenever the caller asked to remove the
/// wished row, including on the repeated submit that found the exclusion already stored — the local
/// removal is a fact of the request, not a count of the rows this call deleted.
/// </summary>
public sealed record WishlistAcquisitionResult(string Outcome, long UserLibraryId, long GameId, bool Dismissed);

/// <summary>
/// The existing owned/subscription row that already covers this game on the chosen platform.
/// <see cref="HasAcquisitionMetadata"/> is what makes a repeated submit idempotent: once the row carries an
/// acquisition store and type, no later submit rewrites them.
///
/// <para>
/// The gate deliberately ignores <c>acquired_store_type</c> and <c>acquisition_provider</c>. Including them
/// would turn every row written before those columns existed into "metadata missing" and let the next
/// submit overwrite the shop and type that acquisition already recorded. A row with no shop and no type is
/// filled once, as a unit, with whatever the submit knows (the two source facts included); a row that
/// already has them keeps its <c>NULL</c> source facts, because the classification of a past purchase is a
/// fact nobody can supply afterwards.
/// </para>
/// </summary>
public sealed record WishlistAcquisitionExistingRow(long UserLibraryId, string State, bool HasAcquisitionMetadata);

/// <summary>
/// Everything the database already knows, reduced to the facts the plan needs. Keeping them a plain record
/// is what makes every acquisition rule deterministic and checkable without a server
/// (<c>checks/WishlistAcquisition.Checks</c>).
/// </summary>
public sealed record WishlistAcquisitionFacts(
    string WishedStore,
    string WishedStoreGameId,
    long CanonicalGameId,
    string Title,
    WishlistAcquisitionExistingRow? ExistingRow,
    IReadOnlyList<string> PlatformExternalIds,
    bool AlreadyExcluded);

/// <summary>
/// The writes one acquisition is allowed to make, decided from the facts alone. The service only executes
/// this; it never decides a rule of its own, so a check that passes here is a check on the real behaviour.
///
/// <para>
/// <see cref="AcquiredStore"/>, <see cref="AcquisitionType"/>, <see cref="AcquiredStoreType"/>,
/// <see cref="AcquisitionProvider"/> and <see cref="AcquisitionDate"/> are one metadata unit: a created row
/// takes all of them, an existing row with no metadata takes all of them once
/// (<see cref="FillAcquisitionMetadata"/>), and nothing else ever writes them. That is why the optional
/// source facts cannot leak onto a row whose acquisition was recorded by an earlier submit.
/// </para>
/// </summary>
public sealed record WishlistAcquisitionPlan(
    string Outcome,
    long UserLibraryId,
    long GameId,
    bool CreateRow,
    bool FillAcquisitionMetadata,
    string Store,
    string StoreGameId,
    string State,
    string Title,
    DateTime AddedAt,
    string AcquiredStore,
    string AcquisitionType,
    string? AcquiredStoreType,
    string? AcquisitionProvider,
    DateOnly? AcquisitionDate,
    bool Dismiss,
    bool InsertExclusion);

/// <summary>
/// Every rule of the wishlist → library acquisition (specs S1/S2/S3/S6), in one pure place.
///
/// <para>
/// Identity is never a title: the canonical game id comes from the claimed Steam mapping, the Steam
/// snapshot's backfilled <c>game_id</c>, or the wished row itself, in that order. The platform is the
/// chosen library (<c>user_library.store</c>), the acquisition store is a separate fact, and the
/// subscription type is the only one that writes <c>state = 'subscription'</c>.
/// </para>
///
/// <para>
/// A canonical game id is <b>never</b> claimed as an external id in a real store namespace (L4). The only
/// claim this feature makes is the wishlist row's own store id — Steam's appid — through
/// <see cref="ClaimFor"/>; every other platform falls back to the manual-row shape
/// (<c>store_game_id = game_id</c>) that the manual and console imports already write, and it writes no
/// mapping at all.
/// </para>
/// </summary>
public static class WishlistAcquisitionRules
{
    /// <summary>
    /// <c>user_library.title</c> is <c>VARCHAR(256)</c>. The name here comes from Steam's snapshot, not from
    /// the client, so an oversized one is truncated instead of failing the insert.
    /// </summary>
    public const int MaxTitleLength = 256;

    /// <summary>
    /// Validates and normalizes one acquisition payload. Throws <see cref="ArgumentException"/> (400) for a
    /// blank or unknown platform, a blank or unknown acquisition store, an unknown acquisition type, an
    /// unknown shop classification or discovery provider, or a future acquisition date: a purchase cannot
    /// have happened tomorrow.
    ///
    /// <para>
    /// The two source facts are optional: <c>null</c> (or an absent field) is the previous payload and stays
    /// null, while a non-null value outside the vocabulary is rejected. A blank string is not "absent" — it
    /// is a value the caller sent that cannot be classified, so it is rejected too instead of being stored
    /// as a silent gap the person never chose.
    /// </para>
    /// </summary>
    public static WishlistAcquisitionInput Validate(WishlistAcquisitionRequest? request, DateOnly today)
    {
        if (request is null)
        {
            throw new ArgumentException("El cuerpo de la solicitud es obligatorio", nameof(request));
        }

        var platform = StoreKeys.Normalize(request.Platform)
            ?? throw new ArgumentException(
                "La plataforma no es una tienda conocida ni un slug de plataforma válido", nameof(request));

        var acquiredStore = StoreKeys.Normalize(request.AcquiredStore)
            ?? throw new ArgumentException(
                "La tienda de adquisición no es una tienda conocida ni un slug de plataforma válido",
                nameof(request));

        var acquisitionType = WishlistAcquisitionTypes.Normalize(request.AcquisitionType)
            ?? throw new ArgumentException(
                $"El tipo de adquisición debe ser uno de: {string.Join(", ", WishlistAcquisitionTypes.All)}",
                nameof(request));

        if (request.AcquiredAt is { } acquiredAt && acquiredAt > today)
        {
            throw new ArgumentException("La fecha de adquisición no puede ser futura", nameof(request));
        }

        // Optional but closed: null/absent means "not registered" (the previous payload and every row written
        // before these columns existed), anything else has to be one of the listed values.
        var acquiredStoreType = request.AcquiredStoreType is null
            ? null
            : WishlistAcquiredStoreTypes.Normalize(request.AcquiredStoreType)
                ?? throw new ArgumentException(
                    $"El tipo de tienda de adquisición debe ser uno de: {string.Join(", ", WishlistAcquiredStoreTypes.All)}",
                    nameof(request));

        var acquisitionProvider = request.AcquisitionProvider is null
            ? null
            : WishlistAcquisitionProviders.Normalize(request.AcquisitionProvider)
                ?? throw new ArgumentException(
                    $"El proveedor de descubrimiento debe ser uno de: {string.Join(", ", WishlistAcquisitionProviders.All)}",
                    nameof(request));

        return new WishlistAcquisitionInput(
            platform,
            acquiredStore,
            acquisitionType,
            request.AcquiredAt,
            request.RemoveFromWishlist,
            acquiredStoreType,
            acquisitionProvider);
    }

    /// <summary>
    /// Library state of an acquired game: a subscription is a catalogue entry, never a purchase; the other
    /// four kinds are ownership.
    /// </summary>
    public static string StateFor(string acquisitionType) => acquisitionType switch
    {
        WishlistAcquisitionTypes.Subscription => LibraryStates.Subscription,
        WishlistAcquisitionTypes.Purchase => LibraryStates.Owned,
        WishlistAcquisitionTypes.Gift => LibraryStates.Owned,
        WishlistAcquisitionTypes.Free => LibraryStates.Owned,
        WishlistAcquisitionTypes.Bundle => LibraryStates.Owned,
        _ => throw new ArgumentException(
            $"Tipo de adquisición desconocido: {acquisitionType}", nameof(acquisitionType))
    };

    /// <summary>
    /// Canonical identity from exact ids only. The claimed Steam mapping is the identity truth (a merge
    /// repoints it), the snapshot's backfilled <c>game_id</c> is second, and the wished row's own
    /// <c>game_id</c> is the last resort. Null means "no canonical identity yet", never "no such game".
    /// </summary>
    public static long? ResolveCanonicalGameId(long? steamMappingGameId, long? snapshotGameId, long? wishedRowGameId)
        => steamMappingGameId ?? snapshotGameId ?? wishedRowGameId;

    /// <summary>
    /// <c>user_library.store_game_id</c> for the acquired row.
    ///
    /// <para>
    /// When the chosen platform is the wishlist's own store, that store id is exact by construction and is
    /// used verbatim (Steam's appid). For any other platform the only exact ids are the ones already claimed
    /// in <c>game_external_ids</c>; the first in ordinal order is taken so the write is reproducible. With
    /// none, the row takes the canonical id — the same shape the manual and console imports write, so both
    /// dedupe on <c>uq_user_library (user_id, store, store_game_id, state)</c>.
    /// </para>
    /// </summary>
    public static string ResolveStoreGameId(
        string platform,
        long canonicalGameId,
        string wishedStore,
        string wishedStoreGameId,
        IReadOnlyList<string> platformExternalIds)
    {
        if (string.Equals(platform, wishedStore, StringComparison.Ordinal))
        {
            return wishedStoreGameId;
        }

        var exact = platformExternalIds
            .Select(id => id?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

        // The canonical-id fallback is deliberately not claimed as an external id: it is a row key, not an
        // identity assertion about the store's catalogue.
        return exact ?? canonicalGameId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The only external-id claim this feature is allowed to make: the wishlist row's own store id, which is
    /// the store's real catalogue id (Steam's appid). Any other platform returns null — writing a canonical
    /// game id into a real store's namespace would assert an identity the store never issued.
    /// </summary>
    public static (string Namespace, string ExternalId)? ClaimFor(
        string platform,
        string wishedStore,
        string wishedStoreGameId)
        => string.Equals(platform, wishedStore, StringComparison.Ordinal)
            ? (platform, wishedStoreGameId)
            : null;

    /// <summary>
    /// Decides the writes of one acquisition: create the row or report the existing one, fill the acquisition
    /// metadata only when the row has none, and remember a local wishlist removal exactly once.
    ///
    /// <para>
    /// The metadata is decided as one unit. A created row carries the shop, the type, the two source facts and
    /// the optional date; an existing row is only filled when it has no shop and no type at all, and even then
    /// it takes the whole unit exactly once. The plan always carries the values of the current submit, so the
    /// service can write the same five fields in both branches without deciding anything: an established
    /// acquisition never receives a source fact from a later submit, because that branch does not fill.
    /// </para>
    /// </summary>
    public static WishlistAcquisitionPlan Plan(
        WishlistAcquisitionInput input,
        WishlistAcquisitionFacts facts,
        DateTime nowUtc)
    {
        var existing = facts.ExistingRow;

        // An existing owned row is not a duplicate to report and not a row to rewrite: an imported or
        // previously acquired row keeps its own state and metadata.
        var outcome = existing is null ? WishlistAcquisitionOutcomes.Created : WishlistAcquisitionOutcomes.AlreadyOwned;
        var state = existing?.State ?? StateFor(input.AcquisitionType);

        return new WishlistAcquisitionPlan(
            Outcome: outcome,
            UserLibraryId: existing?.UserLibraryId ?? 0L,
            GameId: facts.CanonicalGameId,
            CreateRow: existing is null,
            FillAcquisitionMetadata: existing is not null && !existing.HasAcquisitionMetadata,
            Store: input.Platform,
            StoreGameId: ResolveStoreGameId(
                input.Platform,
                facts.CanonicalGameId,
                facts.WishedStore,
                facts.WishedStoreGameId,
                facts.PlatformExternalIds),
            State: state,
            Title: Truncate(facts.Title),
            // A dated acquisition dates the row; an undated one is added when the person registered it —
            // an invented historical date would be a claim the feature cannot support.
            AddedAt: input.AcquiredAt is { } acquiredAt
                ? new DateTime(acquiredAt.Year, acquiredAt.Month, acquiredAt.Day, 0, 0, 0, DateTimeKind.Utc)
                : nowUtc,
            AcquiredStore: input.AcquiredStore,
            AcquisitionType: input.AcquisitionType,
            AcquiredStoreType: input.AcquiredStoreType,
            AcquisitionProvider: input.AcquisitionProvider,
            AcquisitionDate: input.AcquiredAt,
            Dismiss: input.RemoveFromWishlist,
            // The exclusion is written once: the second submit of the same removal finds it and writes nothing.
            InsertExclusion: input.RemoveFromWishlist && !facts.AlreadyExcluded);
    }

    /// <summary>
    /// Display title for an acquisition: Steam's snapshot name when there is one, else the wished row's title
    /// when it is a real name, else <c>null</c>.
    ///
    /// <para>
    /// The wishlist seeds an unknown title with the appid itself, which is a placeholder and not a name: a
    /// canonical game created with it would carry a junk title forever, because nothing rewrites
    /// <c>games.title</c> from <c>steam_games.name</c> later. That case returns null, and the acquisition
    /// answers 404 until Steam has a real name for the appid. No other game's title is ever consulted here:
    /// this is display data, never identity.
    /// </para>
    /// </summary>
    public static string? ResolveAcquisitionTitle(string? snapshotName, string? wishedTitle, string appIdText)
    {
        var snapshot = snapshotName?.Trim();
        if (!string.IsNullOrEmpty(snapshot))
        {
            return snapshot;
        }

        var wished = wishedTitle?.Trim();
        if (!string.IsNullOrEmpty(wished) && !string.Equals(wished, appIdText, StringComparison.Ordinal))
        {
            return wished;
        }

        // Only the appid placeholder (or nothing) is left: not a name.
        return null;
    }

    /// <summary>
    /// Backfill rule for a null canonical link: fill it, never overwrite it. A merge is the owner of an
    /// existing <c>game_id</c>, so writing a value read before the identity gate could resurrect identity the
    /// merge just reassigned.
    /// </summary>
    public static bool ShouldBackfillCanonicalId(long? currentCanonicalGameId)
        => currentCanonicalGameId is null;

    /// <summary>
    /// Answer for a repeated submit whose first run already dismissed the wished row (S3/L4 idempotency).
    ///
    /// <para>
    /// The local exclusion is the only evidence that this appid was ever acquired, and the chosen platform
    /// must already hold the game: without both, an appid the person never wished could be acquired through
    /// this door. Returns <c>null</c> (404) otherwise, and never a write — the first run already did them.
    /// </para>
    /// </summary>
    public static WishlistAcquisitionResult? Replay(
        long canonicalGameId,
        WishlistAcquisitionExistingRow? existingRow,
        bool alreadyExcluded)
        => alreadyExcluded && existingRow is { } existing
            ? new WishlistAcquisitionResult(
                WishlistAcquisitionOutcomes.AlreadyOwned,
                existing.UserLibraryId,
                canonicalGameId,
                // The wished row is gone and the exclusion is stored: the removal already happened.
                Dismissed: true)
            : null;

    private static string Truncate(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        return text.Length <= MaxTitleLength ? text : text[..MaxTitleLength];
    }
}
