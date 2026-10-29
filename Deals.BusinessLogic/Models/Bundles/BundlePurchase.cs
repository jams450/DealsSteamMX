using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// How a tier's membership is decided (spec S4). A tier the provider published without a
/// "choose N of M" rule is <see cref="Fixed"/>: buying it imports every eligible game it lists. A tier
/// with a selection count is <see cref="Choice"/>: the buyer names exactly that many.
/// </summary>
public static class BundlePurchaseSelectionModes
{
    public const string Fixed = "fixed";
    public const string Choice = "choice";
}

/// <summary>What happened to one purchased game on the chosen platform (spec S5).</summary>
public static class BundlePurchaseOutcomes
{
    /// <summary>A new owned library row was created for this game on its platform.</summary>
    public const string Imported = "imported";

    /// <summary>The same canonical game was already owned on that platform: no library write, one duplicate row.</summary>
    public const string Duplicate = "duplicate";
}

/// <summary>
/// Where the canonical game identity of one purchased item came from. <see cref="Itad"/> is the ITAD
/// UUID namespace — the item's own provider id, first-class identity (<c>game_external_ids.namespace =
/// 'itad'</c>). <see cref="Manual"/> is a canonical game the person linked explicitly because the item
/// has no reliable id. A title or a gg.deals URL is never a source of identity.
/// </summary>
public static class BundlePurchaseCanonicalSources
{
    public const string Itad = "itad";
    public const string Manual = "manual";
}

/// <summary>
/// Why a tier cannot be confirmed (spec S4). A blocked tier is never importable: importing an incomplete
/// list, an add-on tier or an empty membership would silently fabricate or lose ownership.
/// </summary>
public static class BundlePurchaseBlockedReasons
{
    public const string ItemsIncomplete = "items_incomplete";
    public const string AddonTier = "addon_tier";
    public const string NoEligibleGames = "no_eligible_games";
    public const string InvalidSelectionCount = "invalid_selection_count";
}

/// <summary>
/// The persisted provider snapshot a purchase is built from: one <c>external_bundles</c> row reduced to
/// the fields the contract needs. It is read once per request, so the preview and the confirm can be
/// compared by hash instead of hoping the upstream refresh did not move underneath.
/// </summary>
public sealed record BundlePurchaseSnapshotRow(
    string Source,
    string BundleKey,
    string Title,
    string? ShopId,
    string? ShopName,
    string? PageUrl,
    string? DealUrl,
    string? Details,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    DateTime ObservedAt,
    string? TiersJson);

/// <summary>One owned library row this user already has, as the duplicate rule needs it.</summary>
public sealed record BundlePurchaseOwnedPair(string Platform, long GameId, long UserLibraryId);

/// <summary>
/// One tier item as the preview returns it. <see cref="ItemKey"/> is stable for the same provider
/// payload: <c>itad:&lt;uuid&gt;</c> when the item carries an ITAD UUID, else a content hash. The key is
/// what the confirm sends back, never a title.
/// </summary>
public sealed record BundlePurchasePreviewItem(
    string ItemKey,
    string Title,
    string? Type,
    int? PriceMinor,
    string? Currency,
    string? ItadGameId,
    string? Url,
    bool Eligible,
    long? CanonicalGameId,
    string? CanonicalSource,
    bool RequiresManualLink,
    bool? Owned,
    long? OwnedUserLibraryId,
    string? OwnedPlatform);

/// <summary>
/// One tier of the preview. <see cref="RequiredSelectionCount"/> is the size a valid confirm must have
/// (every eligible game for a fixed tier, the provider count for a choice tier).
/// </summary>
public sealed record BundlePurchasePreviewTier(
    string TierKey,
    int TierIndex,
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    int? SelectionCount,
    string SelectionMode,
    int RequiredSelectionCount,
    bool Importable,
    string? BlockedReason,
    IReadOnlyList<BundlePurchasePreviewItem> Items);

/// <summary>Read-only purchase preview of one persisted bundle for the caller.</summary>
public sealed record BundlePurchasePreview(
    BundlePurchaseSnapshotRow Snapshot,
    string SnapshotHash,
    IReadOnlyList<BundlePurchasePreviewTier> Tiers)
{
    public bool Purchased { get; init; }

    public long? PurchaseId { get; init; }
}

/// <summary>
/// One selected item, exactly as the client sends it. Every field is nullable because the client is
/// untrusted: <see cref="BundlePurchaseRules.Plan"/> is the only thing that turns this into a plan.
/// </summary>
public sealed record BundlePurchaseItemSelection(string? ItemKey, string? Platform, long? CanonicalGameId);

/// <summary>
/// Raw confirm payload. <see cref="Items"/> may be omitted for a fixed tier (the server then imports the
/// whole eligible membership) but is required for a choice tier.
/// </summary>
public sealed record BundlePurchaseRequest(
    string? Source,
    string? BundleKey,
    string? TierKey,
    string? Platform,
    string? SnapshotHash,
    DateOnly? AcquiredAt,
    string? AcquiredStore,
    string? AcquiredStoreType,
    string? AcquisitionProvider,
    IReadOnlyList<BundlePurchaseItemSelection>? Items);

/// <summary>One validated import item: which item key, which platform and which canonical identity.</summary>
public sealed record BundlePurchasePlanItem(
    string ItemKey,
    string Title,
    string? Type,
    string? ItadGameId,
    long? CanonicalGameId,
    string CanonicalSource,
    string Platform);

/// <summary>
/// The writes one confirm is allowed to make, decided from the snapshot and the request alone. The
/// service executes this plan; it does not decide any rule of its own, so these checks are checks on the
/// real behaviour.
/// </summary>
public sealed record BundlePurchasePlan(
    int UserId,
    string Source,
    string BundleKey,
    string Title,
    string? ShopId,
    string? ShopName,
    string? PageUrl,
    string? DealUrl,
    string? Details,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    DateTime ObservedAt,
    int TierIndex,
    string TierKey,
    int? TierPriceMinor,
    string? TierCurrency,
    bool TierAddon,
    bool TierItemsComplete,
    int? TierSelectionCount,
    string SelectionMode,
    int SelectedCount,
    string Platform,
    string SnapshotHash,
    string SnapshotJson,
    DateOnly? AcquiredAt,
    string? AcquiredStore,
    string? AcquiredStoreType,
    string? AcquisitionProvider,
    IReadOnlyList<BundlePurchasePlanItem> Items);

/// <summary>
/// The Steam wishlist writes a purchase is allowed to make (spec S5): the wished rows to delete and the
/// exclusions that stop the next sync from re-adding them. Built from resolved Steam appids only — a
/// game with no Steam id removes nothing and no appid is invented.
/// </summary>
public sealed record BundlePurchaseWishlistPlan(
    IReadOnlyList<string> Removals,
    IReadOnlyList<string> NewExclusions);

/// <summary>
/// Every rule of the bundle purchase/import contract (specs S3-S6) in one pure place.
///
/// <para>
/// Identity is never a title and never a gg.deals URL: an item with an ITAD UUID is resolved through the
/// <c>itad</c> namespace, and an item without a reliable id must arrive with an explicit
/// <c>canonicalGameId</c> the person picked in the read-only candidate search. A fixed tier imports its
/// whole eligible membership; a choice tier must name exactly the provider's count. Incomplete, add-on
/// and empty tiers are refused rather than imported partially.
/// </para>
///
/// <para>
/// The snapshot hash is the sha256 of a canonical JSON projection of the persisted tier payload plus the
/// bundle identity, so a rewritten provider snapshot — or a hand-edited tier — changes it and the confirm
/// is rejected until the client previews again. The same projection is what the purchase stores, which is
/// what keeps the purchased tier/membership readable after an upstream refresh changes the bundle.
/// </para>
/// </summary>
public static class BundlePurchaseRules
{
    /// <summary><c>user_library.title</c> is VARCHAR(256); bundle titles are display data.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>Only a plain game is imported into the library; DLC, packages and media are not games.</summary>
    public const string EligibleType = "game";

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);

    // --- Persisted tier payload ---------------------------------------------------------------------

    /// <summary>
    /// Reads the persisted tier payload with the same options the writer used. Anything that does not
    /// parse degrades to "no tiers" instead of failing the whole purchase read; a bundle with no tiers is
    /// then simply not confirmable, which is the honest answer.
    /// </summary>
    public static IReadOnlyList<SteamGameBundleTier> ParseTiers(string? tiersJson)
    {
        if (string.IsNullOrWhiteSpace(tiersJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SteamGameBundleTier>>(tiersJson, CanonicalJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Canonical projection of the whole provider snapshot. Written by the preview, repeated verbatim by
    /// the confirm, and persisted on the purchase row so the membership the person bought survives an
    /// upstream refresh.
    /// </summary>
    public static string SnapshotJson(BundlePurchaseSnapshotRow snapshot) =>
        JsonSerializer.Serialize(BuildPayload(snapshot), CanonicalJson);

    /// <summary>sha256 (lowercase hex) of <see cref="SnapshotJson"/>.</summary>
    public static string ComputeSnapshotHash(BundlePurchaseSnapshotRow snapshot) =>
        Sha256Hex(SnapshotJson(snapshot));

    /// <summary>
    /// Stable key of one tier, from its own content only: another bundle with the same tiers produces the
    /// same key, and a changed tier produces a different one.
    /// </summary>
    public static string ComputeTierKey(int tierIndex, SteamGameBundleTier tier) =>
        Sha256Hex(JsonSerializer.Serialize(TierPayloadFor(tierIndex, tier), CanonicalJson));

    /// <summary>
    /// Stable item key: the ITAD UUID when the item has one (identity), else a content hash of title,
    /// type and URL (never identity, just a handle the confirm can repeat).
    /// </summary>
    public static string ComputeItemKey(SteamGameBundleTierGame item)
    {
        var itad = item.ItadGameId?.Trim();
        if (!string.IsNullOrEmpty(itad))
        {
            return "itad:" + itad.ToLowerInvariant();
        }

        var basis = string.Join(
            '\u001f',
            NormalizeKeyPart(item.Title),
            NormalizeKeyPart(item.Type),
            NormalizeKeyPart(item.Url));
        return "item:" + Sha256Hex(basis)[..16];
    }

    // --- Preview ------------------------------------------------------------------------------------

    /// <summary>
    /// Read-only preview of every tier the caller can confirm. <paramref name="platform"/> filters the
    /// ownership reading only; when it is null the owned answer is null ("unknown"), never false.
    /// </summary>
    public static BundlePurchasePreview BuildPreview(
        BundlePurchaseSnapshotRow snapshot,
        string? platform,
        IReadOnlyDictionary<string, long> itadCanonicalIds,
        IReadOnlyList<BundlePurchaseOwnedPair> ownedRows)
    {
        var normalizedPlatform = NormalizePlatformOrNull(platform);
        if (!string.IsNullOrWhiteSpace(platform) && normalizedPlatform is null)
        {
            throw new ArgumentException("La plataforma no es una tienda conocida ni un slug válido", nameof(platform));
        }

        var tiers = ParseTiers(snapshot.TiersJson);
        var ownedByGame = ownedRows
            .Where(row => normalizedPlatform is not null && string.Equals(row.Platform, normalizedPlatform, StringComparison.Ordinal))
            .GroupBy(row => row.GameId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.UserLibraryId).First());

        var previewTiers = new List<BundlePurchasePreviewTier>(tiers.Count);
        for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
        {
            var tier = tiers[tierIndex];
            var items = ProjectItems(tier);
            var eligible = items.Where(entry => IsEligible(entry.Item)).ToList();
            var mode = tier.SelectionCount is null ? BundlePurchaseSelectionModes.Fixed : BundlePurchaseSelectionModes.Choice;
            var blocked = BlockedReason(tier, eligible.Count);

            var previewItems = new List<BundlePurchasePreviewItem>(items.Count);
            foreach (var (key, item) in items)
            {
                var canonical = ResolvePreviewCanonical(item, itadCanonicalIds);
                ownedByGame.TryGetValue(canonical ?? 0, out var ownedRow);
                previewItems.Add(new BundlePurchasePreviewItem(
                    ItemKey: key,
                    Title: item.Title,
                    Type: item.Type,
                    PriceMinor: item.PriceMinor,
                    Currency: item.PriceCurrency,
                    ItadGameId: string.IsNullOrWhiteSpace(item.ItadGameId) ? null : item.ItadGameId.Trim(),
                    Url: item.Url,
                    Eligible: IsEligible(item),
                    CanonicalGameId: canonical,
                    CanonicalSource: string.IsNullOrWhiteSpace(item.ItadGameId)
                        ? null
                        : BundlePurchaseCanonicalSources.Itad,
                    RequiresManualLink: string.IsNullOrWhiteSpace(item.ItadGameId),
                    Owned: normalizedPlatform is null || canonical is null ? null : ownedRow is not null,
                    OwnedUserLibraryId: ownedRow?.UserLibraryId,
                    OwnedPlatform: ownedRow is null ? null : normalizedPlatform));
            }

            previewTiers.Add(new BundlePurchasePreviewTier(
                TierKey: ComputeTierKey(tierIndex, tier),
                TierIndex: tierIndex,
                PriceMinor: tier.PriceMinor,
                Currency: tier.Currency,
                Addon: tier.Addon,
                ItemsComplete: tier.ItemsComplete,
                SelectionCount: tier.SelectionCount,
                SelectionMode: mode,
                RequiredSelectionCount: blocked == BundlePurchaseBlockedReasons.InvalidSelectionCount
                    ? 0
                    : RequiredSelectionCount(mode, tier.SelectionCount, eligible.Count),
                Importable: blocked is null,
                BlockedReason: blocked,
                Items: previewItems));
        }

        return new BundlePurchasePreview(snapshot, ComputeSnapshotHash(snapshot), previewTiers);
    }

    // --- Confirm ------------------------------------------------------------------------------------

    /// <summary>
    /// Validates one confirm against the current persisted snapshot and reduces it to a plan. Throws
    /// <see cref="ArgumentException"/> (400) for anything the request gets wrong, including a stale
    /// snapshot hash, a tier key that no longer exists, a selection that does not match the tier's rule,
    /// an unidentified item without an explicit link, and a link to a canonical game that does not exist.
    /// </summary>
    public static BundlePurchasePlan Plan(
        BundlePurchaseRequest? request,
        BundlePurchaseSnapshotRow snapshot,
        int userId,
        DateOnly today,
        IReadOnlySet<long> existingCanonicalGameIds)
    {
        if (request is null)
        {
            throw new ArgumentException("El cuerpo de la solicitud es obligatorio", nameof(request));
        }

        if (!string.Equals(request.Source?.Trim(), snapshot.Source, StringComparison.Ordinal))
        {
            throw new ArgumentException("El bundle de la solicitud no coincide con el snapshot", nameof(request));
        }

        if (!string.Equals(request.BundleKey?.Trim(), snapshot.BundleKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("El bundle de la solicitud no coincide con el snapshot", nameof(request));
        }

        var platform = NormalizePlatformOrNull(request.Platform)
            ?? throw new ArgumentException(
                "La plataforma no es una tienda conocida ni un slug de plataforma válido", nameof(request));

        var snapshotHash = ComputeSnapshotHash(snapshot);
        if (!string.Equals(request.SnapshotHash?.Trim(), snapshotHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "El snapshot del bundle cambió desde la vista previa; vuelve a consultarlo antes de confirmar",
                nameof(request));
        }

        var acquiredAt = request.AcquiredAt;
        if (acquiredAt is { } date && date > today)
        {
            throw new ArgumentException("La fecha de adquisición no puede ser futura", nameof(request));
        }

        var acquiredStore = request.AcquiredStore is null ? null : NormalizeRequired(request.AcquiredStore, "tienda de adquisición", nameof(request));
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
        if (acquiredStore is null && (acquiredStoreType is not null || acquisitionProvider is not null))
        {
            throw new ArgumentException(
                "La clasificación y el proveedor de la adquisición requieren la tienda de adquisición",
                nameof(request));
        }

        var tiers = ParseTiers(snapshot.TiersJson);
        var tierIndex = FindTierIndex(tiers, request.TierKey);
        if (tierIndex < 0)
        {
            throw new ArgumentException(
                "El tier de la solicitud ya no existe en el bundle; vuelve a consultar la vista previa",
                nameof(request));
        }

        var tier = tiers[tierIndex];
        var items = ProjectItems(tier);
        var eligible = items.Where(entry => IsEligible(entry.Item)).ToList();
        var blocked = BlockedReason(tier, eligible.Count);
        if (blocked is not null)
        {
            throw new ArgumentException(
                $"El tier no se puede importar ({blocked}): el bundle no publica una lista de juegos completa",
                nameof(request));
        }

        var mode = tier.SelectionCount is null ? BundlePurchaseSelectionModes.Fixed : BundlePurchaseSelectionModes.Choice;
        var selections = NormalizeSelections(request.Items, items);
        var eligibleKeys = eligible.Select(entry => entry.Key).ToList();

        if (mode == BundlePurchaseSelectionModes.Fixed)
        {
            if (selections.Count > 0 && !selections.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(eligibleKeys))
            {
                throw new ArgumentException(
                    "El tier es de compra completa: la selección debe incluir todos sus juegos",
                    nameof(request));
            }

            // No selection is the whole membership, in tier order. It is only answerable when every
            // eligible game already has reliable identity: an item without an ITAD id needs an explicit
            // canonical link, and filling it in silently would assert an identity nobody chose.
            if (selections.Count == 0)
            {
                if (eligible.Any(entry => string.IsNullOrWhiteSpace(entry.Item.ItadGameId)))
                {
                    throw new ArgumentException(
                        "El tier incluye juegos sin identificador de proveedor: envía la selección completa con sus vínculos canónicos",
                        nameof(request));
                }

                foreach (var key in eligibleKeys)
                {
                    selections[key] = new BundlePurchaseItemSelection(key, null, null);
                }
            }
        }
        else
        {
            var required = tier.SelectionCount!.Value;
            if (selections.Count != required)
            {
                throw new ArgumentException(
                    $"El tier permite elegir exactamente {required} juego(s) y se recibieron {selections.Count}",
                    nameof(request));
            }
        }

        var planItems = new List<BundlePurchasePlanItem>(selections.Count);
        foreach (var (key, item) in eligible)
        {
            if (!selections.TryGetValue(key, out var selection))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                throw new ArgumentException(
                    "El bundle publica un juego sin título; no se puede importar una fila sin nombre",
                    nameof(request));
            }

            var itemPlatform = selection.Platform is null
                ? platform
                : NormalizePlatformOrNull(selection.Platform)
                    ?? throw new ArgumentException(
                        "La plataforma del juego no es una tienda conocida ni un slug de plataforma válido",
                        nameof(request));

            var hasItad = !string.IsNullOrWhiteSpace(item.ItadGameId);
            if (hasItad)
            {
                if (selection.CanonicalGameId is not null)
                {
                    throw new ArgumentException(
                        "Un juego con UUID de ITAD no acepta un identificador canónico del cliente",
                        nameof(request));
                }

                planItems.Add(new BundlePurchasePlanItem(
                    key,
                    item.Title,
                    item.Type,
                    item.ItadGameId!.Trim(),
                    CanonicalGameId: null,
                    CanonicalSource: BundlePurchaseCanonicalSources.Itad,
                    itemPlatform));
                continue;
            }

            if (selection.CanonicalGameId is not { } manualId || !existingCanonicalGameIds.Contains(manualId))
            {
                throw new ArgumentException(
                    "Un juego sin identificador de proveedor necesita un vínculo canónico explícito y existente",
                    nameof(request));
            }

            planItems.Add(new BundlePurchasePlanItem(
                key,
                item.Title,
                item.Type,
                ItadGameId: null,
                CanonicalGameId: manualId,
                CanonicalSource: BundlePurchaseCanonicalSources.Manual,
                itemPlatform));
        }

        if (planItems.Count != selections.Count)
        {
            throw new ArgumentException("La selección incluye un ítem que no es un juego del tier", nameof(request));
        }

        return new BundlePurchasePlan(
            UserId: userId,
            Source: snapshot.Source,
            BundleKey: snapshot.BundleKey,
            Title: snapshot.Title,
            ShopId: snapshot.ShopId,
            ShopName: snapshot.ShopName,
            PageUrl: snapshot.PageUrl,
            DealUrl: snapshot.DealUrl,
            Details: snapshot.Details,
            PublishedAt: snapshot.PublishedAt,
            ExpiresAt: snapshot.ExpiresAt,
            ObservedAt: snapshot.ObservedAt,
            TierIndex: tierIndex,
            TierKey: ComputeTierKey(tierIndex, tier),
            TierPriceMinor: tier.PriceMinor,
            TierCurrency: tier.Currency,
            TierAddon: tier.Addon,
            TierItemsComplete: tier.ItemsComplete,
            TierSelectionCount: tier.SelectionCount,
            SelectionMode: mode,
            SelectedCount: planItems.Count,
            Platform: platform,
            SnapshotHash: snapshotHash,
            SnapshotJson: SnapshotJson(snapshot),
            AcquiredAt: acquiredAt,
            AcquiredStore: acquiredStore,
            AcquiredStoreType: acquiredStoreType,
            AcquisitionProvider: acquisitionProvider,
            Items: planItems);
    }

    // --- Duplicate inventory and visibility ----------------------------------------------------------

    /// <summary>
    /// A game already owned on the chosen platform is a duplicate; another platform is not (spec S5).
    /// </summary>
    public static string OutcomeFor(bool alreadyOwned) =>
        alreadyOwned ? BundlePurchaseOutcomes.Duplicate : BundlePurchaseOutcomes.Imported;

    /// <summary>
    /// The library row's store id for a bundle import. No store issued a new id for a game that came inside
    /// a bundle, so nothing is claimed beyond what the catalog already knows:
    /// <list type="bullet">
    /// <item>a known id of the chosen store (today the Steam appid) is used verbatim, because it is exact
    /// identity and keeps a later Steam/original sync on the same row;</item>
    /// <item>Steam without a known appid gets a <c>bundle:</c> prefixed key. A bare canonical id there would
    /// parse as an appid and misclaim a Steam identity the catalog never proved;</item>
    /// <item>every other platform uses the canonical id text — the same shape the manual and console imports
    /// write — and duplication is decided by canonical game + store, never by this key alone.</item>
    /// </list>
    /// </summary>
    public static string StoreGameIdFor(string platform, long canonicalGameId, string? platformExternalId = null)
    {
        var exact = platformExternalId?.Trim();
        if (!string.IsNullOrEmpty(exact))
        {
            return exact;
        }

        var canonical = canonicalGameId.ToString(CultureInfo.InvariantCulture);
        return string.Equals(platform, StoreKeys.Steam, StringComparison.Ordinal)
            ? "bundle:" + canonical
            : canonical;
    }

    /// <summary>
    /// True when a purchase of the first bundle must hide the second from the caller's pending catalog.
    /// <c>itad</c> and <c>itad_global</c> are the same provider reporting the same bundle, so they share a
    /// family; gg.deals is its own key space and is never collapsed with an ITAD purchase.
    /// </summary>
    public static bool HidesBundle(
        string purchasedSource,
        string purchasedBundleKey,
        string candidateSource,
        string candidateBundleKey) =>
        string.Equals(SourceFamily(purchasedSource), SourceFamily(candidateSource), StringComparison.Ordinal) &&
        string.Equals(purchasedBundleKey, candidateBundleKey, StringComparison.Ordinal);

    /// <summary>
    /// The Steam wishlist writes of a completed purchase: every acquired game that resolves to a wished
    /// appid is removed once, and each removal gets a protection exclusion so the next sync cannot
    /// re-insert it. Duplicates take part too — the game was acquired inside the bundle either way.
    /// </summary>
    public static BundlePurchaseWishlistPlan PlanWishlist(
        IReadOnlyCollection<long> acquiredGameIds,
        IReadOnlyDictionary<long, string> steamAppIdByGame,
        IReadOnlySet<string> wishedAppIds,
        IReadOnlySet<string> excludedAppIds)
    {
        var appIds = acquiredGameIds
            .Distinct()
            .Select(gameId => steamAppIdByGame.TryGetValue(gameId, out var appId) ? appId?.Trim() : null)
            .Where(appId => !string.IsNullOrEmpty(appId))
            .Select(appId => appId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(appId => appId, StringComparer.Ordinal)
            .ToList();

        var removals = appIds.Where(wishedAppIds.Contains).ToList();
        var newExclusions = removals.Where(appId => !excludedAppIds.Contains(appId)).ToList();
        return new BundlePurchaseWishlistPlan(removals, newExclusions);
    }

    // --- Internals ----------------------------------------------------------------------------------

    private sealed record ItemPayload(
        string ItemKey,
        string Title,
        string? Type,
        int? PriceMinor,
        string? PriceCurrency,
        string? ItadGameId,
        string? Url);

    private sealed record TierPayload(
        int Index,
        string TierKey,
        int? PriceMinor,
        string? Currency,
        bool Addon,
        bool ItemsComplete,
        int? SelectionCount,
        IReadOnlyList<ItemPayload> Games);

    private sealed record TierHashPayload(
        int Index,
        int? PriceMinor,
        string? Currency,
        bool Addon,
        bool ItemsComplete,
        int? SelectionCount,
        IReadOnlyList<ItemPayload> Games);

    private sealed record SnapshotPayload(
        string Source,
        string BundleKey,
        string Title,
        string? ShopId,
        string? ShopName,
        string? PageUrl,
        string? DealUrl,
        string? Details,
        string? PublishedAt,
        string? ExpiresAt,
        string ObservedAt,
        IReadOnlyList<TierPayload> Tiers);

    private static SnapshotPayload BuildPayload(BundlePurchaseSnapshotRow snapshot)
    {
        var tiers = ParseTiers(snapshot.TiersJson);
        var projected = new List<TierPayload>(tiers.Count);
        for (var index = 0; index < tiers.Count; index++)
        {
            var tier = tiers[index];
            projected.Add(new TierPayload(
                index,
                ComputeTierKey(index, tier),
                tier.PriceMinor,
                tier.Currency,
                tier.Addon,
                tier.ItemsComplete,
                tier.SelectionCount,
                ProjectItems(tier).Select(entry => ToPayload(entry.Key, entry.Item)).ToList()));
        }

        return new SnapshotPayload(
            snapshot.Source,
            snapshot.BundleKey,
            snapshot.Title,
            snapshot.ShopId,
            snapshot.ShopName,
            snapshot.PageUrl,
            snapshot.DealUrl,
            snapshot.Details,
            Timestamp(snapshot.PublishedAt),
            Timestamp(snapshot.ExpiresAt),
            Timestamp(snapshot.ObservedAt) ?? string.Empty,
            projected);
    }

    private static TierHashPayload TierPayloadFor(int tierIndex, SteamGameBundleTier tier) =>
        new(
            tierIndex,
            tier.PriceMinor,
            tier.Currency,
            tier.Addon,
            tier.ItemsComplete,
            tier.SelectionCount,
            ProjectItems(tier).Select(entry => ToPayload(entry.Key, entry.Item)).ToList());

    private static ItemPayload ToPayload(string itemKey, SteamGameBundleTierGame item) =>
        new(
            itemKey,
            item.Title,
            item.Type,
            item.PriceMinor,
            item.PriceCurrency,
            string.IsNullOrWhiteSpace(item.ItadGameId) ? null : item.ItadGameId.Trim(),
            item.Url);

    /// <summary>
    /// Projects a tier's items once, keeping both the stable key and the item. Repeated content inside one
    /// tier is disambiguated with a deterministic suffix so two distinct items never share a key.
    /// </summary>
    private static IReadOnlyList<(string Key, SteamGameBundleTierGame Item)> ProjectItems(SteamGameBundleTier tier)
    {
        var games = tier.Games ?? [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var projected = new List<(string Key, SteamGameBundleTierGame Item)>(games.Count);
        for (var index = 0; index < games.Count; index++)
        {
            var key = ComputeItemKey(games[index]);
            if (!used.Add(key))
            {
                key = key + "#" + used.Count.ToString(CultureInfo.InvariantCulture);
                used.Add(key);
            }

            projected.Add((key, games[index]));
        }

        return projected;
    }

    private static Dictionary<string, BundlePurchaseItemSelection> NormalizeSelections(
        IReadOnlyList<BundlePurchaseItemSelection>? selections,
        IReadOnlyList<(string Key, SteamGameBundleTierGame Item)> items)
    {
        var byKey = items.ToDictionary(entry => entry.Key, entry => entry.Item, StringComparer.Ordinal);
        var normalized = new Dictionary<string, BundlePurchaseItemSelection>(StringComparer.Ordinal);
        if (selections is null)
        {
            return normalized;
        }

        foreach (var selection in selections)
        {
            if (selection is null)
            {
                throw new ArgumentException("Cada juego seleccionado necesita su clave de ítem", nameof(selections));
            }

            var key = selection.ItemKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Cada juego seleccionado necesita su clave de ítem", nameof(selections));
            }

            if (!byKey.TryGetValue(key, out var item))
            {
                throw new ArgumentException(
                    "La selección incluye un ítem que ya no pertenece al tier; vuelve a consultar la vista previa",
                    nameof(selections));
            }

            if (!IsEligible(item))
            {
                throw new ArgumentException(
                    "La selección incluye un ítem que no es un juego",
                    nameof(selections));
            }

            if (!normalized.TryAdd(key, selection))
            {
                throw new ArgumentException("La selección repite un ítem", nameof(selections));
            }
        }

        return normalized;
    }

    private static bool IsEligible(SteamGameBundleTierGame item) =>
        string.Equals(item.Type?.Trim(), EligibleType, StringComparison.OrdinalIgnoreCase);

    private static int FindTierIndex(IReadOnlyList<SteamGameBundleTier> tiers, string? tierKey)
    {
        var key = tierKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (var index = 0; index < tiers.Count; index++)
        {
            if (string.Equals(ComputeTierKey(index, tiers[index]), key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? BlockedReason(SteamGameBundleTier tier, int eligibleCount)
    {
        if (!tier.ItemsComplete)
        {
            return BundlePurchaseBlockedReasons.ItemsIncomplete;
        }

        if (tier.Addon)
        {
            return BundlePurchaseBlockedReasons.AddonTier;
        }

        if (eligibleCount == 0)
        {
            return BundlePurchaseBlockedReasons.NoEligibleGames;
        }

        if (tier.SelectionCount is { } count && (count < 1 || count > eligibleCount))
        {
            return BundlePurchaseBlockedReasons.InvalidSelectionCount;
        }

        return null;
    }

    private static int RequiredSelectionCount(string mode, int? selectionCount, int eligibleCount) =>
        mode == BundlePurchaseSelectionModes.Fixed
            ? eligibleCount
            : Math.Max(0, selectionCount ?? 0);

    private static long? ResolvePreviewCanonical(
        SteamGameBundleTierGame item,
        IReadOnlyDictionary<string, long> itadCanonicalIds)
    {
        var itad = item.ItadGameId?.Trim();
        if (string.IsNullOrEmpty(itad))
        {
            return null;
        }

        return itadCanonicalIds.TryGetValue(itad, out var gameId)
            ? gameId
            : itadCanonicalIds.TryGetValue(itad.ToLowerInvariant(), out gameId) ? gameId : null;
    }

    private static string? NormalizePlatformOrNull(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? null : StoreKeys.Normalize(platform);

    private static string NormalizeRequired(string? value, string label, string parameter)
    {
        var normalized = StoreKeys.Normalize(value)
            ?? throw new ArgumentException(
                $"El/la {label} no es una tienda conocida ni un slug de plataforma válido",
                parameter);
        return normalized;
    }

    private static string SourceFamily(string source) =>
        string.Equals(source, BundleCatalogOrder.ItadSource, StringComparison.Ordinal) ||
        string.Equals(source, BundleCatalogOrder.ItadGlobalSource, StringComparison.Ordinal)
            ? BundleCatalogOrder.ItadSource
            : source;

    private static string NormalizeKeyPart(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string? Timestamp(DateTime? value) =>
        value is { } timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : null;

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
