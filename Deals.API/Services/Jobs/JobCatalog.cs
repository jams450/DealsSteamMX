using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Services;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Stable machine keys of the jobs registered in the host. They are the values already written to
/// <c>job_runs.job</c> by the scheduled services, so the history of an old row still matches the catalog
/// entry: renaming one of these renames history.
/// </summary>
public static class JobKeys
{
    public const string WishlistSync = JobRunLog.WishlistSync;
    public const string FxRateRefresh = JobRunLog.FxRateRefresh;
    public const string GlobalBundles = GlobalBundleSyncService.JobName;
    public const string GgDealsBundles = GgDealsBundleSyncService.JobName;
    public const string GlobalDeals = GlobalDealSyncService.JobName;

    /// <summary>Ntfy deal alerts. Was never recorded before the manual dispatcher existed.</summary>
    public const string NtfyAlerts = "ntfy.alerts";

    /// <summary>Daily retention purge of <c>job_runs</c>. Was never recorded before this unit.</summary>
    public const string Purge = "jobs.purge";

    /// <summary>
    /// Manual-only pending-price pass (spec S5). Deliberately absent from every schedule: it is queued by a
    /// human from the admin page and never by the host. It is scoped to that human's wishlist.
    /// </summary>
    public const string CatalogPricesRefresh = "catalog.prices-refresh";

    public static readonly IReadOnlyList<string> All =
    [
        WishlistSync,
        FxRateRefresh,
        GlobalBundles,
        GgDealsBundles,
        GlobalDeals,
        NtfyAlerts,
        Purge,
        CatalogPricesRefresh
    ];
}

/// <summary>
/// Option keys the manual dispatcher understands. All but one are switches; <c>catalog.prices-refresh</c> also takes
/// the bounded number of hours the requester wants re-priced (spec S2).
/// </summary>
public static class JobOptionKeys
{
    /// <summary><c>wishlist.sync</c>: also run the expensive per-game price pass.</summary>
    public const string IncludePriceRefresh = "includePriceRefresh";

    /// <summary><c>ntfy.alerts</c>: run the outbox producer.</summary>
    public const string Produce = "produce";

    /// <summary><c>ntfy.alerts</c>: run the outbox dispatcher (the phase that actually sends).</summary>
    public const string Dispatch = "dispatch";

    /// <summary><c>jobs.purge</c>: count what the purge would delete, deleting nothing.</summary>
    public const string DryRun = "dryRun";

    /// <summary>
    /// <c>catalog.prices-refresh</c>: how many hours of price age the requester wants re-priced (spec S1/S2). A game
    /// whose cheapest stored price is older than this, or that has no price at all, is pending.
    /// </summary>
    public const string AgeHours = "ageHours";
}

/// <summary>
/// The frozen, additive option types of the admin contract. <see cref="Boolean"/> is what every option was before
/// this unit, and it stays the meaning of a descriptor or payload that does not say otherwise.
/// </summary>
public static class JobOptionTypes
{
    public const string Boolean = "boolean";

    public const string Integer = "integer";

    public static bool IsInteger(string? type) =>
        string.Equals(type, Integer, StringComparison.Ordinal);
}

/// <summary>
/// One option of one job, as the catalog publishes it (spec S5). The contract is additive: <see cref="Key"/>,
/// <see cref="Label"/>, <see cref="Help"/> and <see cref="DefaultValue"/> keep their names and meaning, and
/// <see cref="Type"/> (defaulting to <c>boolean</c>), <see cref="MinValue"/> and <see cref="MaxValue"/> are new. A
/// boolean option therefore serialises exactly as it did, plus an explicit <c>type</c>; an integer option publishes
/// its bounds so the page can render an hours input without knowing the job.
/// </summary>
/// <param name="DefaultValue">
/// The value a run gets when the option is absent. Boxed <c>bool</c> for a boolean option, boxed <c>int</c> for an
/// integer one — that is what keeps the old payload shape working while the new option carries a number.
/// </param>
public sealed record JobOptionDescriptor(
    string Key,
    string Label,
    string Help,
    object DefaultValue,
    string Type = JobOptionTypes.Boolean,
    int? MinValue = null,
    int? MaxValue = null);

/// <summary>
/// One row of the admin catalog. <see cref="AdvisoryLockKey"/> is the same PostgreSQL session lock the
/// scheduled service takes, so a manual run can never overlap a scheduled one — or a second manual one.
/// </summary>
/// <param name="RequiresRequester">
/// True when the job reads one person's data. The manual dispatcher then refuses to start it without the
/// authenticated requester captured from the access token, and the executor never falls back to a wider scope.
/// </param>
public sealed record JobDefinition(
    string Job,
    string DisplayName,
    string Description,
    bool Enabled,
    string? DisabledReason,
    string Schedule,
    long? AdvisoryLockKey,
    bool Destructive,
    bool RequiresConfirmation,
    IReadOnlyList<JobOptionDescriptor> Options)
{
    public bool RequiresRequester { get; init; }
}

/// <summary>
/// Plain snapshot of the configuration the catalog exposes. A record instead of eight
/// <c>IOptions&lt;T&gt;</c> dependencies so the catalog is a pure value that checks can construct.
/// </summary>
public sealed record JobCatalogSettings(
    bool WishlistEnabled,
    int WishlistRunAtHour,
    string WishlistTimeZoneId,
    bool GlobalBundlesEnabled,
    int GlobalBundlesRefreshHours,
    bool GgDealsBundlesEnabled,
    int GgDealsBundlesRefreshHours,
    bool GlobalDealsEnabled,
    int GlobalDealsRefreshHours,
    bool NtfyEnabled,
    int NtfyPollSeconds,
    string FxBaseCurrency,
    string FxQuoteCurrency,
    int RetentionDays)
{
    public static JobCatalogSettings Default { get; } = new(
        WishlistEnabled: true,
        WishlistRunAtHour: 3,
        WishlistTimeZoneId: "America/Mexico_City",
        GlobalBundlesEnabled: true,
        GlobalBundlesRefreshHours: 12,
        GgDealsBundlesEnabled: false,
        GgDealsBundlesRefreshHours: 24,
        GlobalDealsEnabled: false,
        GlobalDealsRefreshHours: 24,
        NtfyEnabled: false,
        NtfyPollSeconds: 900,
        FxBaseCurrency: "USD",
        FxQuoteCurrency: "MXN",
        RetentionDays: 1);
}

/// <summary>
/// The jobs the admin page may inspect and, when it is safe, launch by hand: the seven scheduled cycles plus
/// the manual-only pending-price pass of the requester's wishlist.
///
/// The catalog is data, not behaviour: it says what each job is, whether it can run right now, which lock
/// a manual run must take and which bounded options it accepts. Execution lives in the job executors; the
/// scheduled hosted services and the manual dispatcher both call them, so a manual run is the same work as
/// a scheduled one instead of a second implementation.
///
/// A disabled job is listed with the reason it is disabled and cannot be launched. Hiding it would make
/// the catalog lie about what the host contains; offering it would make a switch look decorative.
/// </summary>
public sealed class JobCatalog
{
    private readonly IReadOnlyDictionary<string, JobDefinition> _byKey;

    public JobCatalog(JobCatalogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        All =
        [
            new JobDefinition(
                JobKeys.WishlistSync,
                "Wishlist (lista y precios)",
                "Importa la lista de deseados de Steam y, si se pide, refresca los precios de cada juego.",
                settings.WishlistEnabled,
                settings.WishlistEnabled ? null : "Wishlist:Enabled está en false: el job no se ejecuta en este host.",
                $"diario a las {settings.WishlistRunAtHour:00}:00 ({settings.WishlistTimeZoneId})",
                WishlistSyncJobExecutor.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                [
                    new JobOptionDescriptor(
                        JobOptionKeys.IncludePriceRefresh,
                        "Incluir refresco de precios",
                        "Añade el pase de precios por juego. Puede tardar decenas de minutos; sin esta opción solo se sincroniza la lista.",
                        DefaultValue: false)
                ]),
            new JobDefinition(
                JobKeys.FxRateRefresh,
                "Tipo de cambio FX",
                $"Consulta y guarda el tipo de cambio {settings.FxBaseCurrency}/{settings.FxQuoteCurrency} usado para convertir precios.",
                Enabled: true,
                DisabledReason: null,
                Schedule: "cada 24 h",
                FxRateRefreshJobExecutor.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                []),
            new JobDefinition(
                JobKeys.GlobalBundles,
                "Bundles globales (ITAD)",
                "Recorre el catálogo paginado de bundles de ITAD y reemplaza la instantánea guardada.",
                settings.GlobalBundlesEnabled,
                settings.GlobalBundlesEnabled ? null : "GlobalBundles:Enabled está en false: el job no se ejecuta en este host.",
                $"cada {settings.GlobalBundlesRefreshHours} h",
                GlobalBundleSyncService.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                []),
            new JobDefinition(
                JobKeys.GgDealsBundles,
                "Bundles de gg.deals",
                "Recorre el catálogo de bundles activos de gg.deals y reemplaza la instantánea guardada.",
                settings.GgDealsBundlesEnabled,
                settings.GgDealsBundlesEnabled ? null : "GgDealsBundles:Enabled está en false: el job no se ejecuta en este host.",
                $"cada {settings.GgDealsBundlesRefreshHours} h",
                GgDealsBundleSyncService.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                []),
            new JobDefinition(
                JobKeys.GlobalDeals,
                "Ofertas globales (ITAD)",
                "Recorre el feed de ofertas de ITAD y reemplaza la instantánea guardada que alimenta /deals.",
                settings.GlobalDealsEnabled,
                settings.GlobalDealsEnabled ? null : "GlobalDeals:Enabled está en false: el job no se ejecuta en este host.",
                $"cada {settings.GlobalDealsRefreshHours} h",
                GlobalDealSyncService.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                []),
            new JobDefinition(
                JobKeys.NtfyAlerts,
                "Alertas ntfy",
                "Genera alertas de ofertas a partir de las instantáneas guardadas y envía las que estén pendientes.",
                settings.NtfyEnabled,
                settings.NtfyEnabled ? null : "Ntfy:Enabled está en false: el job no se ejecuta y no se envía ningún aviso.",
                $"cada {settings.NtfyPollSeconds} s",
                NtfyDealsJobExecutor.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                [
                    new JobOptionDescriptor(
                        JobOptionKeys.Produce,
                        "Generar alertas",
                        "Lee las instantáneas guardadas y encola avisos nuevos. No consulta proveedores.",
                        DefaultValue: true),
                    new JobOptionDescriptor(
                        JobOptionKeys.Dispatch,
                        "Enviar pendientes",
                        "Envía los avisos encolados a ntfy. Cada aviso ya enviado no se repite.",
                        DefaultValue: true)
                ]),
            new JobDefinition(
                JobKeys.Purge,
                "Purga del historial",
                "Borra las ejecuciones terminadas más antiguas que la retención configurada. Las que siguen en curso nunca se borran.",
                Enabled: true,
                DisabledReason: null,
                Schedule: "cada 24 h",
                JobRunPurgeJobExecutor.AdvisoryLockKey,
                Destructive: true,
                RequiresConfirmation: true,
                [
                    new JobOptionDescriptor(
                        JobOptionKeys.DryRun,
                        "Simulación (no borra)",
                        $"Cuenta lo que se borraría con la retención actual ({settings.RetentionDays} día(s)) sin eliminar nada.",
                        DefaultValue: false)
                ]),
            new JobDefinition(
                JobKeys.CatalogPricesRefresh,
                "Precios pendientes de mi wishlist",
                "Refresca los precios de hasta " +
                    $"{CatalogPriceRefreshLimits.BatchSize} juegos de tu lista de deseados cuyo precio " +
                    "comparable en MXN es más antiguo que la antigüedad que elijas o no existe. Solo mira tu lista: " +
                    "no toca tu biblioteca, tus favoritos ni los de otras cuentas. Se ejecuta solo a mano y no " +
                    "envía avisos.",
                Enabled: true,
                DisabledReason: null,
                Schedule: "solo manual (sin ejecución automática)",
                CatalogPriceRefreshJobExecutor.AdvisoryLockKey,
                Destructive: false,
                RequiresConfirmation: false,
                [
                    // The one bounded choice of this job (spec S2/S5). It is a window, never a batch size: the
                    // 20-game bound stays a constant so no payload can widen it into a whole-wishlist sweep.
                    new JobOptionDescriptor(
                        JobOptionKeys.AgeHours,
                        "Antigüedad mínima (horas)",
                        "Solo se refrescan los juegos de tu lista de deseados cuyo precio guardado es más antiguo " +
                            $"que estas horas (entre {CatalogPriceRefreshLimits.MinAgeHours} y " +
                            $"{CatalogPriceRefreshLimits.MaxAgeHours}) o que no tiene precio. Los juegos sin precio " +
                            "se incluyen siempre. Es independiente del monitor, que siempre usa " +
                            $"{CatalogFreshness.Hours} h.",
                        DefaultValue: CatalogPriceRefreshLimits.DefaultAgeHours,
                        Type: JobOptionTypes.Integer,
                        MinValue: CatalogPriceRefreshLimits.MinAgeHours,
                        MaxValue: CatalogPriceRefreshLimits.MaxAgeHours)
                ])
            {
                // The batch belongs to the person who asked for it: the requester comes from the access token
                // and is required, so there is no catalog-wide run and no fallback account.
                RequiresRequester = true
            }
        ];

        _byKey = All.ToDictionary(entry => entry.Job, StringComparer.Ordinal);
    }

    public IReadOnlyList<JobDefinition> All { get; }

    /// <summary>Ordinal lookup: the keys are machine identifiers, not user text.</summary>
    public JobDefinition? Find(string? job) =>
        !string.IsNullOrWhiteSpace(job) && _byKey.TryGetValue(job, out var definition) ? definition : null;
}
