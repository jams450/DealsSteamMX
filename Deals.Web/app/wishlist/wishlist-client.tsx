"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import type { ColumnDef, FilterFn, SortingFn } from "@tanstack/react-table";
import { Gamepad2, RefreshCw } from "lucide-react";
import { DataGrid } from "@/components/data-grid/data-grid";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { refreshSteamGame } from "@/app/steam/_lib/steam-api";
import { formatCurrency } from "@/lib/format/currency";
import { cn } from "@/lib/ui/cn";
import { getWishlist, syncWishlist } from "./_lib/wishlist-api";
import type { WishlistItem, WishlistResponse, WishlistState, WishlistSyncResponse } from "./_lib/wishlist-contract";

const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

// Los mínimos de tiendas ya vienen convertidos por el backend: su moneda es siempre MXN y no se
// presenta como aproximación porque no lo es.
const MXN = "MXN";

// El normalizador ya descarta fechas inválidas; el guard evita que Intl.format lance si algo se cuela.
function formatDateTime(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormatter.format(date);
}

// Mismo criterio que el detalle del juego (`game-client.tsx`): se formatea en la moneda original del
// dato, sin convertir. Una moneda que no es MXN se ve distinta porque Intl imprime su código
// (p.ej. "XYZ 1,234.00") en vez de "MX$". Sin importe o sin moneda no hay nada que formatear: "—".
function formatMinor(amountMinor: number | null, currency: string | null) {
  if (amountMinor === null || currency === null) return null;
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

// La prioridad de Steam es un rango 0-based (0 = el primero de la lista); se muestra tal cual llega.
function formatPriority(value: number | null) {
  return value === null ? "—" : String(value);
}

// `refreshSteamGame` solo propaga el mensaje del BFF: el status (429 del limitador o 503 por defecto
// del middleware) se pierde en el helper. El aviso de espera se muestra siempre y el copy de límite
// solo cuando el propio mensaje ya lo delata.
const RATE_LIMIT_PATTERN = /429|503|demasiad|l[ií]mite|rate|too many/i;
const RATE_LIMIT_HINT =
  "Los refrescos por juego están limitados a 6 por minuto por IP: si se alcanzó el límite, espera un minuto y vuelve a intentar.";

function rowRefreshError(cause: unknown): string {
  const message = cause instanceof Error ? cause.message.trim() : "";
  if (RATE_LIMIT_PATTERN.test(message)) {
    return "Se alcanzó el límite de refrescos (6 por minuto por IP). Espera un minuto y vuelve a intentar.";
  }
  return message ? `${message} ${RATE_LIMIT_HINT}` : `No se pudieron actualizar los precios. ${RATE_LIMIT_HINT}`;
}

function withoutRowError(current: Readonly<Record<number, string>>, appId: number) {
  if (!(appId in current)) return current;
  const next = { ...current };
  delete next[appId];
  return next;
}

// 120x45 es el tamaño nativo de Steam; 90x34 en móvil para que la fila siga cabiendo a 360px. La
// portada es decorativa (`alt=""`) porque el nombre del juego va al lado como texto.
function WishlistThumb({ src, className }: { readonly src: string | null; readonly className?: string }) {
  const [failed, setFailed] = useState(false);
  const image = src && !failed ? src : null;

  return (
    <span
      className={cn(
        "inline-flex h-[34px] w-[90px] shrink-0 items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] sm:h-[45px] sm:w-[120px]",
        className
      )}
    >
      {image ? (
        <img
          src={image}
          alt=""
          width={120}
          height={45}
          loading="lazy"
          decoding="async"
          onError={() => setFailed(true)}
          className="h-full w-full object-cover"
        />
      ) : (
        <Gamepad2 className="h-4 w-4 text-muted sm:h-5 sm:w-5" aria-hidden="true" />
      )}
    </span>
  );
}

// `itadGameId` es la identidad canónica del juego: sin ella, el juego no entra en la comparación ni en
// las alertas. Se dice tal cual en vez de dejar la celda vacía.
function ItadBadge({ itadGameId }: { readonly itadGameId: string | null }) {
  return itadGameId ? (
    <span className="tabler-badge tabler-badge-info">Identificado en ITAD</span>
  ) : (
    <span className="tabler-badge tabler-badge-muted">Sin identificar en ITAD</span>
  );
}

interface PriceValueProps {
  readonly amountMinor: number | null;
  readonly currency: string | null;
}

// Un precio sin dato no se rellena con 0 ni se marca como gratis: lee "—" en tono muted.
function PriceValue({ amountMinor, currency }: PriceValueProps) {
  const display = formatMinor(amountMinor, currency);
  return display === null ? (
    <span className="text-muted">—</span>
  ) : (
    <span className="deal-price text-primary">{display}</span>
  );
}

interface PriceFactProps extends PriceValueProps {
  readonly label: string;
}

// Versión para las tiles móviles: etiqueta arriba, importe abajo, dos por fila a 360px.
function PriceFact({ label, amountMinor, currency }: PriceFactProps) {
  return (
    <div className="min-w-0">
      <p className="text-xs text-muted">{label}</p>
      <PriceValue amountMinor={amountMinor} currency={currency} />
    </div>
  );
}

// Cada estado explica qué pasa y qué hacer. La wishlist privada es la trampa real de la API de Steam:
// devuelve lo mismo que una wishlist sin juegos, así que nunca se muestra como "0 juegos".
function StateNotice({ state }: { readonly state: WishlistState }) {
  if (state === "no_steam_id") {
    return (
      <Alert variant="info">
        <p className="text-sm font-semibold text-primary">Falta configurar tu SteamID64.</p>
        <p className="mt-1 text-sm text-secondary">
          La wishlist se importa desde Steam y la cuenta todavía no tiene su SteamID64 (17 dígitos). Se
          configura en el perfil del usuario.
        </p>
        <Link
          href="/users"
          className="btn-secondary-semantic mt-3 inline-flex h-10 items-center px-4 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
        >
          Configurar en Usuarios
        </Link>
      </Alert>
    );
  }

  if (state === "inaccessible") {
    return (
      <Alert variant="info">
        <p className="text-sm font-semibold text-primary">
          La wishlist es privada o el perfil no es accesible.
        </p>
        <p className="mt-1 text-sm text-secondary">
          Steam no devuelve ningún juego cuando el perfil o la wishlist no son públicos. Para poder
          importarla, tu perfil y tu wishlist de Steam deben ser públicas.
        </p>
      </Alert>
    );
  }

  if (state === "never_synced") {
    return (
      <Alert variant="info">
        <p className="text-sm font-semibold text-primary">Aún no hay ninguna sincronización.</p>
        <p className="mt-1 text-sm text-secondary">
          Esta cuenta todavía no ha importado su wishlist de Steam. Usa «Sincronizar ahora» para traerla.
        </p>
      </Alert>
    );
  }

  return null;
}

function WishlistRowMeta({ item }: { readonly item: WishlistItem }) {
  const added = formatDateTime(item.addedAt);
  const refreshed = formatDateTime(item.refreshedAt);

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
      <span>Prioridad {formatPriority(item.priority)}</span>
      <span>Alta {added ?? "—"}</span>
      <span>Actualizado {refreshed ?? "—"}</span>
      <ItadBadge itadGameId={item.itadGameId} />
    </div>
  );
}

function SyncReport({ report }: { readonly report: WishlistSyncResponse }) {
  const syncedDisplay = formatDateTime(report.syncedAt);

  return (
    <section className="app-card space-y-3 p-4" aria-labelledby="wishlist-report-heading" aria-live="polite">
      <h3 id="wishlist-report-heading" className="text-sm font-semibold tracking-tight text-primary">
        Resultado de la sincronización
      </h3>
      <ul className="flex flex-wrap items-center gap-2">
        <li><span className="tabler-badge tabler-badge-success">Importados {report.added}</span></li>
        <li><span className="tabler-badge tabler-badge-info">Traídos de Steam {report.fetchedFromSteam}</span></li>
        <li>
          <span className={cn("tabler-badge", report.fetchFailed > 0 ? "tabler-badge-danger" : "tabler-badge-muted")}>
            Fallidos de Steam {report.fetchFailed}
          </span>
        </li>
        <li><span className="tabler-badge tabler-badge-info">Actualizados {report.updated}</span></li>
        <li><span className="tabler-badge tabler-badge-muted">Eliminados {report.removed}</span></li>
        <li><span className="tabler-badge tabler-badge-info">Refrescados {report.refreshed}</span></li>
        <li>
          <span className={cn("tabler-badge", report.failed > 0 ? "tabler-badge-danger" : "tabler-badge-muted")}>
            Fallidos {report.failed}
          </span>
        </li>
        <li><span className="tabler-badge tabler-badge-muted">{report.itemCount} en la wishlist</span></li>
      </ul>
      {/* «Refrescados» y «Fallidos» son de precios: el sync no los toca, por eso van en 0. */}
      <p className="text-xs text-muted">
        Esta sincronización solo importa la lista y trae de Steam los juegos que faltaban. No refresca
        precios de ofertas: eso es el botón «Sincronizar» de cada juego y la actualización diaria, así que
        «Refrescados» y «Fallidos» van en 0.
      </p>
      <p className="text-xs text-muted">
        {syncedDisplay ? `Última sincronización ${syncedDisplay}` : "El servidor no informó la fecha de sincronización"}
      </p>
    </section>
  );
}

interface RowRefreshButtonProps {
  readonly item: WishlistItem;
  readonly refreshing: boolean;
  readonly blocked: boolean;
  readonly onRefresh: (item: WishlistItem) => void;
}

function RowRefreshButton({ item, refreshing, blocked, onRefresh }: RowRefreshButtonProps) {
  return (
    <Button
      type="button"
      variant="secondary"
      className="h-9 px-3 text-xs"
      loading={refreshing}
      loadingText="Actualizando"
      disabled={blocked}
      aria-label={`Sincronizar los precios de ${item.name} (AppID ${item.appId})`}
      onClick={() => onRefresh(item)}
    >
      <RefreshCw className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      Sincronizar
    </Button>
  );
}

interface WishlistItemsProps {
  readonly items: readonly WishlistItem[];
  readonly refreshingAppId: number | null;
  readonly rowErrors: Readonly<Record<number, string>>;
  readonly onRefresh: (item: WishlistItem) => void;
}

function WishlistItems({ items, refreshingAppId, rowErrors, onRefresh }: WishlistItemsProps) {
  const [filter, setFilter] = useState("");
  const query = filter.trim().toLocaleLowerCase("es-MX");
  const filteredItems = useMemo(
    () => items.filter((item) => item.name.toLocaleLowerCase("es-MX").includes(query) || String(item.appId).includes(query)),
    [items, query]
  );

  const wishlistFilter: FilterFn<WishlistItem> = (row, _columnId, value) => {
    const text = String(value).trim().toLocaleLowerCase("es-MX");
    return row.original.name.toLocaleLowerCase("es-MX").includes(text) || String(row.original.appId).includes(text);
  };

  // Un valor ausente se expresa como `undefined`, nunca `null`, y cada columna ordenable lleva
  // `sortUndefined: "last"`. El paquete resuelve ese caso con un `return` temprano ANTES de invertir
  // por dirección, así que los juegos sin precio quedan al final tanto en asc como en desc. El default
  // (`sortUndefined: 1`) sí se invierte: con él, ordenar descendente pone arriba los juegos sin precio
  // como si fueran los más caros. Un comparador propio no puede arreglarlo; recibe la columna, no la
  // dirección, y su resultado se invierte igual. `0` se conserva: un juego gratis ordena como el menor.
  const numericSort: SortingFn<WishlistItem> = (rowA, rowB, columnId) =>
    Number(rowA.getValue(columnId)) - Number(rowB.getValue(columnId));

  const columns = useMemo<ColumnDef<WishlistItem>[]>(() => [
    { id: "cover", header: "Portada", enableSorting: false, cell: ({ row }) => <WishlistThumb src={row.original.imageUrl} /> },
    {
      accessorKey: "name", header: "Juego", sortingFn: (rowA, rowB, id) => String(rowA.getValue(id)).localeCompare(String(rowB.getValue(id)), "es-MX"),
      cell: ({ row }) => <div className="min-w-48"><Link href={`/games/${row.original.appId}`} className="text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">{row.original.name}</Link><p className="text-xs text-muted">AppID {row.original.appId}</p><ItadBadge itadGameId={row.original.itadGameId} />{rowErrors[row.original.appId] ? <p role="alert" className="mt-1 text-xs text-danger">{rowErrors[row.original.appId]}</p> : null}</div>
    },
    { id: "priority", accessorFn: (item) => item.priority ?? undefined, header: "Prioridad", sortingFn: numericSort, sortUndefined: "last" },
    { id: "addedAt", accessorFn: (item) => item.addedAt ? new Date(item.addedAt).getTime() : undefined, header: "Alta", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => formatDateTime(row.original.addedAt) ?? "—" },
    { id: "refreshedAt", accessorFn: (item) => item.refreshedAt ? new Date(item.refreshedAt).getTime() : undefined, header: "Actualizado", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => formatDateTime(row.original.refreshedAt) ?? "—" },
    { id: "basePriceMinor", accessorFn: (item) => item.basePriceMinor ?? undefined, header: "Precio base", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.basePriceMinor} currency={row.original.baseCurrency} /> },
    { id: "historyLowMinor", accessorFn: (item) => item.historyLowMinor ?? undefined, header: "Mínimo histórico", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.historyLowMinor} currency={row.original.historyLowCurrency} /> },
    { id: "bestOfficialMinor", accessorFn: (item) => item.bestOfficialMinor ?? undefined, header: "Mín. oficial", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.bestOfficialMinor} currency={MXN} /> },
    { id: "bestKeyshopMinor", accessorFn: (item) => item.bestKeyshopMinor ?? undefined, header: "Mín. keys", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.bestKeyshopMinor} currency={MXN} /> },
    { id: "actions", header: "Acciones", enableSorting: false, cell: ({ row }) => <RowRefreshButton item={row.original} refreshing={refreshingAppId === row.original.appId} blocked={refreshingAppId !== null && refreshingAppId !== row.original.appId} onRefresh={onRefresh} /> }
  ], [onRefresh, refreshingAppId, rowErrors]);

  return (
    <section className="app-card space-y-4 p-5" aria-labelledby="wishlist-items-heading">
      <div className="space-y-1">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">Juegos seguidos</p>
        <h2 id="wishlist-items-heading" className="text-xl font-semibold tracking-tight text-primary">En tu wishlist</h2>
        <p className="tabler-badge tabler-badge-muted">{filteredItems.length} de {items.length} juegos</p>
        <p className="text-xs text-muted">«Mín. oficial» y «Mín. keys» ya están en MXN. El precio base y el mínimo histórico se muestran en la moneda del proveedor, sin convertir.</p>
      </div>
      <div className="md:hidden">
        <label className="sr-only" htmlFor="wishlist-filter-mobile">Buscar por nombre o AppID</label>
        <input id="wishlist-filter-mobile" type="search" value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="Buscar por nombre o AppID" className="input-semantic h-8 w-full text-xs" />
      </div>
      <ul className="space-y-3 md:hidden">
        {filteredItems.map((item) => (
          <li key={item.appId} className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3">
            <div className="flex items-start gap-2"><WishlistThumb src={item.imageUrl} /><div className="min-w-0"><Link href={`/games/${item.appId}`} className="text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">{item.name}</Link><p className="text-xs text-muted">AppID {item.appId}</p><ItadBadge itadGameId={item.itadGameId} /></div></div>
            <div className="mt-3 grid grid-cols-2 gap-x-3 gap-y-2"><PriceFact label="Precio base" amountMinor={item.basePriceMinor} currency={item.baseCurrency} /><PriceFact label="Mínimo histórico" amountMinor={item.historyLowMinor} currency={item.historyLowCurrency} /><PriceFact label="Mín. oficial" amountMinor={item.bestOfficialMinor} currency={MXN} /><PriceFact label="Mín. keys" amountMinor={item.bestKeyshopMinor} currency={MXN} /></div>
            <div className="mt-3"><WishlistRowMeta item={item} /></div>
            <div className="mt-3"><RowRefreshButton item={item} refreshing={refreshingAppId === item.appId} blocked={refreshingAppId !== null && refreshingAppId !== item.appId} onRefresh={onRefresh} /></div>
            {rowErrors[item.appId] ? <p role="alert" className="mt-2 text-xs text-danger">{rowErrors[item.appId]}</p> : null}
          </li>
        ))}
      </ul>
      {filteredItems.length === 0 ? <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">{items.length === 0 ? "La wishlist está vacía." : "Ningún juego coincide con la búsqueda."}</p> : null}
      <DataGrid
        columns={columns}
        rows={[...items]}
        density="compact"
        stickyHeader
        stickyActionsColumn
        manualPagination
        enableGlobalFilter
        globalFilterPlaceholder="Buscar por nombre o AppID"
        globalFilterFn={wishlistFilter}
        emptyMessage={items.length === 0 ? "La wishlist está vacía." : "Ningún juego coincide con la búsqueda."}
      />
    </section>
  );
}

export function WishlistClient() {
  const [wishlist, setWishlist] = useState<WishlistResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncError, setSyncError] = useState<string | null>(null);
  const [report, setReport] = useState<WishlistSyncResponse | null>(null);
  const [refreshingAppId, setRefreshingAppId] = useState<number | null>(null);
  const [rowErrors, setRowErrors] = useState<Readonly<Record<number, string>>>({});

  async function loadWishlist() {
    setLoading(true);
    setError(null);
    try {
      setWishlist(await getWishlist());
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo cargar la wishlist.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void loadWishlist();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sincronizar la lista no vuelve a `loading`: la lista ya cargada se queda en pantalla mientras corre.
  async function runSync() {
    if (syncing) return;
    setSyncing(true);
    setSyncError(null);
    setReport(null);
    try {
      setReport(await syncWishlist());
      try {
        setWishlist(await getWishlist());
      } catch {
        // La sincronización ya terminó: no se borra el reporte, solo se dice que faltó recargar.
        setSyncError("La sincronización terminó, pero no se pudo recargar la lista.");
      }
    } catch (cause) {
      setSyncError(cause instanceof Error ? cause.message : "No se pudo sincronizar la wishlist.");
    } finally {
      setSyncing(false);
    }
  }

  // Refresca un solo juego (Steam + ITAD + gg.deals) con el endpoint del detalle. Al terminar se recarga
  // la lista: es un cambio de estado, no una navegación, así que el scroll se queda donde estaba.
  async function refreshItem(item: WishlistItem) {
    if (refreshingAppId !== null) return;
    setRefreshingAppId(item.appId);
    setRowErrors((current) => withoutRowError(current, item.appId));
    try {
      await refreshSteamGame(item.appId);
    } catch (cause) {
      setRowErrors((current) => ({ ...current, [item.appId]: rowRefreshError(cause) }));
      setRefreshingAppId(null);
      return;
    }

    try {
      setWishlist(await getWishlist());
    } catch {
      setSyncError("Los precios se actualizaron, pero no se pudo recargar la lista. Recarga la página para verlos.");
    } finally {
      setRefreshingAppId(null);
    }
  }

  if (loading) return <p className="app-card p-5 text-sm text-muted">Cargando...</p>;

  if (error || !wishlist) {
    return (
      <div className="space-y-3">
        <Alert variant="danger">{error ?? "No se pudo cargar la wishlist."}</Alert>
        <Button type="button" variant="secondary" onClick={() => void loadWishlist()}>
          Reintentar
        </Button>
      </div>
    );
  }

  const items = wishlist.items;
  const syncedDisplay = formatDateTime(wishlist.syncedAt);
  const syncingAllowed = wishlist.state !== "no_steam_id";

  return (
    <div className="space-y-4">
      <section className="app-card-accent space-y-4 p-5" aria-labelledby="wishlist-summary-heading">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Sincronización</p>
            <h2 id="wishlist-summary-heading" className="text-xl font-semibold tracking-tight text-primary">
              Wishlist de Steam
            </h2>
            <p className="text-xs text-muted">
              La sincronización importa tu wishlist de Steam: añade los juegos nuevos y trae de Steam los
              que todavía no tenían ficha local, con su prioridad y su fecha de alta. No actualiza precios
              de ofertas (ITAD y gg.deals): eso es el botón «Sincronizar» de cada juego, además de la
              actualización diaria.
            </p>
          </div>
          <div className="flex flex-col items-start gap-1 sm:items-end">
            {syncingAllowed ? (
              <Button
                type="button"
                variant="secondary"
                className="w-full sm:w-auto"
                loading={syncing}
                loadingText="Sincronizando..."
                onClick={() => void runSync()}
              >
                Sincronizar ahora
              </Button>
            ) : null}
            <span className="text-xs text-muted" aria-live="polite">
              {syncing ? "Consultando la wishlist de Steam..." : ""}
            </span>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {wishlist.state === "ok" ? (
            <span className="tabler-badge tabler-badge-muted">
              {items.length === 1 ? "1 juego" : `${items.length} juegos`}
            </span>
          ) : null}
          {syncedDisplay ? (
            <span className="tabler-badge tabler-badge-info">Última sincronización {syncedDisplay}</span>
          ) : (
            <span className="tabler-badge tabler-badge-warning">Sin fecha de sincronización</span>
          )}
        </div>

        {syncError ? <Alert variant="danger">{syncError}</Alert> : null}
        {report ? <SyncReport report={report} /> : null}
      </section>

      <StateNotice state={wishlist.state} />

      {wishlist.state === "ok" && items.length === 0 ? (
        <div className="app-card space-y-1 p-5">
          <p className="text-sm font-semibold text-primary">Tu wishlist de Steam está vacía.</p>
          <p className="text-sm text-muted">
            No hay juegos que seguir todavía. Añade juegos a tu wishlist en Steam y vuelve a sincronizar.
          </p>
        </div>
      ) : null}

      {items.length > 0 ? (
        <WishlistItems
          items={items}
          refreshingAppId={refreshingAppId}
          rowErrors={rowErrors}
          onRefresh={(item) => void refreshItem(item)}
        />
      ) : null}
    </div>
  );
}
