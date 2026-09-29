"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { memo, useCallback, useEffect, useId, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { ArrowDown, ArrowUp, Gamepad2, RefreshCw, Search, SlidersHorizontal, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { PriceFact, formatMinor } from "@/components/ui/price-value";
import { refreshSteamGame } from "@/app/steam/_lib/steam-api";
import { cn } from "@/lib/ui/cn";
import { ToastStack } from "@/components/feedback/toast-stack";
import { useToasts } from "@/components/feedback/use-toasts";
import { assignWishlistCategory, createWishlistCategory, getWishlist, previewWishlistPackage, removeWishlistCategoryItems, replaceWishlistItemCategories, serializeWishlistQuery, syncWishlist, updateWishlistPreferences, type WishlistQuery } from "./_lib/wishlist-api";
import type { PaginationState, SortingState } from "@tanstack/react-table";
import { dealScore, discountPercent } from "./_lib/wishlist-metrics";
import {
  MAX_PACKAGE_APP_IDS,
  exceedsPackageLimit,
  packageRequestAppIds,
  pageSelectionState,
  setAppIds,
  toggleAppId
} from "./_lib/wishlist-package";
import { SYNC_STORES, syncStamp } from "./_lib/wishlist-sync";
import type {
  WishlistItem,
  WishlistPackagePreview,
  WishlistResponse,
  WishlistState,
  WishlistSyncResponse
} from "./_lib/wishlist-contract";

const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });
const shortDateFormatter = new Intl.DateTimeFormat("es-MX", { day: "2-digit", month: "short" });

// Los mínimos de tiendas ya vienen convertidos por el backend: su moneda es siempre MXN y no se
// presenta como aproximación porque no lo es.
const MXN = "MXN";

// Preferencias puramente visuales: viven en localStorage. El umbral de descuento no está aquí porque
// ese es del backend (se guarda con `updateWishlistPreferences`).
const WISHLIST_PAGE_SIZES = [10, 25, 50, 100];
const PAGE_SIZE_STORAGE_KEY = "wishlist.pageSize.v1";
const WISHLIST_SORTS = ["priority", "name", "bestPrice", "bestDiscount", "officialDiscount", "keyshopDiscount", "officialPrice", "keyshopPrice"] as const;
type WishlistSort = (typeof WISHLIST_SORTS)[number];
type TriState = "all" | "yes" | "no";
type WishlistFilters = {
  search: string;
  minPrice: string;
  maxPrice: string;
  owned: TriState;
  subscription: TriState;
  categoryIds: number[];
  uncategorized: boolean;
};
const DEFAULT_FILTERS: WishlistFilters = { search: "", minPrice: "", maxPrice: "", owned: "all", subscription: "all", categoryIds: [], uncategorized: false };

function filtersFromUrl(params: Pick<URLSearchParams, "get" | "getAll">): WishlistFilters {
  // Number(null) is 0. Treating a missing URL parameter as zero silently applied a 0–0 MXN range.
  const number = (name: string) => {
    const raw = params.get(name);
    if (raw === null || raw === "") return "";
    const value = Number(raw);
    return Number.isSafeInteger(value) && value >= 0 ? String(value) : "";
  };
  return {
    search: (params.get("search") ?? "").slice(0, 100), minPrice: number("minPrice"), maxPrice: number("maxPrice"),
    owned: params.get("owned") === "yes" || params.get("owned") === "no" ? params.get("owned")! as TriState : "all",
    subscription: params.get("subscription") === "yes" || params.get("subscription") === "no" ? params.get("subscription")! as TriState : "all",
    categoryIds: [...new Set(params.getAll("categoryIds").map(Number).filter((id) => Number.isSafeInteger(id) && id > 0))].slice(0, 50),
    uncategorized: params.get("uncategorized") === "true"
  };
}

function queryFilters(filters: WishlistFilters): Omit<WishlistQuery, "page" | "pageSize" | "sort" | "direction"> {
  const minPrice = filters.minPrice === "" ? undefined : Number(filters.minPrice);
  const maxPrice = filters.maxPrice === "" ? undefined : Number(filters.maxPrice);
  return { search: filters.search.trim() || undefined, minPrice, maxPrice, owned: filters.owned === "all" ? undefined : filters.owned, subscription: filters.subscription === "all" ? undefined : filters.subscription, categoryIds: filters.categoryIds.length ? filters.categoryIds : undefined, uncategorized: filters.uncategorized || undefined };
}

function hasDefaultFilters(filters: WishlistFilters) {
  return filters.search.trim() === "" &&
    filters.minPrice === "" &&
    filters.maxPrice === "" &&
    filters.owned === "all" &&
    filters.subscription === "all" &&
    filters.categoryIds.length === 0 &&
    !filters.uncategorized;
}

// El normalizador ya descarta fechas inválidas; el guard evita que Intl.format lance si algo se cuela.
function formatDateTime(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormatter.format(date);
}

// El formateo de importes (`formatMinor`, `PriceValue`, `PriceFact`) vive en
// `components/ui/price-value.tsx` y lo comparten esta página y la biblioteca.

// La prioridad de Steam no se usa para ordenar por defecto, así que en la tarjeta vive como dato
// discreto junto al AppID, no como columna destacada.
const RATE_LIMIT_HINT =
  "Los refrescos por juego están limitados a 6 por minuto por IP: si se alcanzó el límite, espera un minuto y vuelve a intentar.";

function rowRefreshError(cause: unknown): string {
  if (cause && typeof cause === "object" && "code" in cause && cause.code === "RATE_LIMITED") {
    return RATE_LIMIT_HINT;
  }

  const message = cause instanceof Error ? cause.message.trim() : "";
  return message || "No se pudieron actualizar los precios.";
}

function withoutRowError(current: Readonly<Record<number, string>>, appId: number) {
  if (!(appId in current)) return current;
  const next = { ...current };
  delete next[appId];
  return next;
}

// El tamaño nativo de `tiny_image` hace que la carátula sea el ancla visual de la tarjeta sin
// convertir la grilla de tres columnas en una lista. La portada es decorativa (`alt=""`).
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

// La fila dice qué proveedor está sincronizado y cuándo, uno por uno: un proveedor caído conserva su
// fecha vieja mientras los demás avanzan, y eso es justo lo que hay que poder ver. El ITAD sustituye al
// badge «Identificado en ITAD»: la identidad del juego es una de las cosas que esta columna informa.
// El mapeo proveedor → campo vive en `_lib/wishlist-sync.ts`, con test propio.

// Short date keeps the five-provider matrix readable without widening the table.
function formatShortDate(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : shortDateFormatter.format(date);
}

const SYNC_SHORT_LABELS = {
  steam: "St",
  itad: "IT",
  ggdeals: "GG",
  epic: "Ep",
  microsoft: "MS"
} as const;

function SyncBadges({ item }: { readonly item: WishlistItem }) {
  return (
    <div className="w-fit min-w-[13rem]" aria-label="Fechas de sincronización por proveedor">
      <div className="grid grid-cols-5 gap-x-1 text-center text-[0.625rem] font-semibold leading-4 text-muted" aria-hidden="true">
        {SYNC_STORES.map((store) => (
          <span key={store.key}>{SYNC_SHORT_LABELS[store.key]}</span>
        ))}
      </div>
      <div className="grid grid-cols-5 gap-x-1 text-center text-[0.625rem] tabular-nums leading-4">
        {SYNC_STORES.map((store) => {
          const stamp = formatShortDate(syncStamp(item, store));
          return (
            <span
              key={store.key}
              className={cn("rounded-sm px-0.5", stamp ? "text-secondary" : "text-muted")}
              title={stamp ? `${store.label}: última sincronización ${stamp}` : `${store.label}: sin sincronizar`}
            >
              {stamp ?? "—"}
              <span className="sr-only">{`${store.label}: ${stamp ? `última sincronización ${stamp}` : "sin sincronización"}`}</span>
            </span>
          );
        })}
      </div>
    </div>
  );
}

// Misma forma que `PriceFact` para los valores que no son importes (porcentajes y scores).
function MetricFact({ label, children }: { readonly label: string; readonly children: ReactNode }) {
  return (
    <div className="min-w-0">
      <p className="text-xs text-muted">{label}</p>
      {children}
    </div>
  );
}

// El descuento se calcula contra el precio base de Steam: positivo cuando el mejor precio es más bajo
// (se pinta "-42.5%") y negativo cuando es más caro (se pinta "+3.0%"). Es un porcentaje, no un importe.
function formatDiscountPercent(value: number | null) {
  if (value === null) return "—";
  const sign = value > 0 ? "-" : value < 0 ? "+" : "";
  return `${sign}${Math.abs(value).toFixed(1)}%`;
}

function DiscountValue({ value }: { readonly value: number | null }) {
  return value === null ? (
    <span className="text-muted">—</span>
  ) : (
    <span className="text-primary">{formatDiscountPercent(value)}</span>
  );
}

// Banda discreta del score: solo cambia el tono del badge, el número siempre está escrito.
function scoreBadgeTone(score: number) {
  if (score >= 7) return "tabler-badge-success";
  if (score >= 4) return "tabler-badge-info";
  return "tabler-badge-muted";
}

function ScoreValue({ score }: { readonly score: number | null }) {
  return score === null ? (
    <span className="text-muted">—</span>
  ) : (
    <span className={cn("tabler-badge", scoreBadgeTone(score))}>{score.toFixed(1)}</span>
  );
}

// Un score por banda: el descuento se mide contra el precio base de Steam y el bonus por cercanía al
// mínimo histórico entra aunque el descuento no llegue al umbral.
function itemScore(item: WishlistItem, bestMinor: number | null, minViableDiscountPercent: number): number | null {
  return dealScore({
    basePriceMinor: item.basePriceMinor,
    baseCurrency: item.baseCurrency,
    bestMinor,
    historyLowMinor: item.historyLowMinor,
    historyLowCurrency: item.historyLowCurrency,
    minViableDiscountPercent
  });
}

// Los cuatro valores que no son importes directos (descuentos y scores): viven en la zona de
// «Más detalle» de la tarjeta compacta.
function MobileMetrics({ item, minViableDiscountPercent }: { readonly item: WishlistItem; readonly minViableDiscountPercent: number }) {
  return (
    <>
      <MetricFact label="% dto. oficial">
        <DiscountValue value={discountPercent(item.basePriceMinor, item.baseCurrency, item.bestOfficialMinor)} />
      </MetricFact>
      <MetricFact label="% dto. keys">
        <DiscountValue value={discountPercent(item.basePriceMinor, item.baseCurrency, item.bestKeyshopMinor)} />
      </MetricFact>
      <MetricFact label="Deal oficial">
        <ScoreValue score={itemScore(item, item.bestOfficialMinor, minViableDiscountPercent)} />
      </MetricFact>
      <MetricFact label="Deal keys">
        <ScoreValue score={itemScore(item, item.bestKeyshopMinor, minViableDiscountPercent)} />
      </MetricFact>
    </>
  );
}

interface ThresholdControlProps {
  readonly value: number;
  readonly onCommit: (next: number) => Promise<void>;
}

// El umbral vive en el backend: se edita aquí y se confirma al salir del campo o con Enter. Si el PUT
// falla, el campo vuelve al valor vigente y el error queda en línea, sin tocar el resto de la página.
function ThresholdControl({ value, onCommit }: ThresholdControlProps) {
  const inputId = useId();
  const [draft, setDraft] = useState(String(value));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setDraft(String(value));
  }, [value]);

  async function commit() {
    const parsed = Number(draft);
    if (!Number.isSafeInteger(parsed) || parsed < 0 || parsed > 95) {
      setError("Escribe un número entero entre 0 y 95.");
      return;
    }
    if (parsed === value) {
      setError(null);
      setDraft(String(value));
      return;
    }

    setSaving(true);
    setError(null);
    try {
      await onCommit(parsed);
    } catch (cause) {
      setDraft(String(value));
      setError(cause instanceof Error && cause.message ? cause.message : "No se pudo guardar el descuento mínimo viable.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-2">
      <label htmlFor={inputId} className="text-xs font-medium text-secondary">
        Descuento mínimo viable %
      </label>
      <input
        id={inputId}
        type="number"
        inputMode="numeric"
        min={0}
        max={95}
        step={1}
        value={draft}
        disabled={saving}
        onChange={(event) => setDraft(event.target.value)}
        onBlur={() => void commit()}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            void commit();
          }
        }}
        className="input-semantic h-8 w-20 text-xs"
      />
      <span className="text-xs text-muted">Ajusta los dos scores de deal. Se guarda con Enter o al salir del campo.</span>
      <span className="sr-only" aria-live="polite">
        {saving ? "Guardando el descuento mínimo viable..." : ""}
      </span>
      {error ? (
        <p role="alert" className="text-xs text-danger">
          {error}
        </p>
      ) : null}
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
      <span>Alta {added ?? "—"}</span>
      <span>Actualizado {refreshed ?? "—"}</span>
      <SyncBadges item={item} />
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

// El cálculo del paquete lo hace el servidor, con debounce: marcar varios juegos seguidos no debe
// convertirse en una petición por clic. Mientras llega, la barra no muestra el importe anterior como si
// fuera el nuevo.
const PACKAGE_DEBOUNCE_MS = 350;

interface PackageScenario {
  readonly subtotalMinor: number | null;
  readonly quoted: number;
  readonly missing: number;
}

// Un escenario del paquete. `scenario === null` es "todavía no se sabe" (cálculo en vuelo): no se pinta el
// número viejo ni un 0. Un subtotal `null` con escenario resuelto es "ningún juego cotizado", que se lee
// literal, nunca como un importe.
function PackageScenarioValue({
  label,
  scenario,
  currency
}: {
  readonly label: string;
  readonly scenario: PackageScenario | null;
  readonly currency: string;
}) {
  const display = scenario === null ? null : formatMinor(scenario.subtotalMinor, currency);
  const total = scenario === null ? null : scenario.quoted + scenario.missing;

  return (
    <div className="min-w-0 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3">
      <p className="text-xs font-semibold uppercase tracking-widest text-muted">{label}</p>
      {scenario === null ? (
        <p className="text-lg font-semibold text-muted">Calculando...</p>
      ) : display === null ? (
        <p className="text-lg font-semibold text-muted">Sin cotizar</p>
      ) : (
        <p className="deal-price text-lg font-semibold text-primary">{display}</p>
      )}
      <p className="text-xs text-muted">
        {scenario === null
          ? "Contando juegos..."
          : `${scenario.quoted} de ${total} cotizados${scenario.missing > 0 ? ` · ${scenario.missing} sin cotizar` : ""}`}
      </p>
    </div>
  );
}

interface PackageSummaryBarProps {
  readonly selectedCount: number;
  readonly preview: WishlistPackagePreview | null;
  readonly loading: boolean;
  readonly error: string | null;
  readonly reconciledAppIds: readonly number[];
  readonly onClear: () => void;
}

// Los dos subtotales son escenarios ALTERNATIVOS, no parciales de un total: nunca se suman entre sí. Por eso
// no hay campo combinado, cada uno vive en su tarjeta y la nota lo dice con palabras.
function PackageSummaryBar({
  selectedCount,
  preview,
  loading,
  error,
  reconciledAppIds,
  onClear
}: PackageSummaryBarProps) {
  const currency = preview?.currency ?? MXN;
  const pricedAt = preview ? formatDateTime(preview.pricedAt) : null;
  const scenariosReady = preview !== null && !loading;

  return (
    <section
      className="app-card-accent space-y-3 p-4"
      aria-labelledby="wishlist-package-heading"
      aria-live="polite"
      aria-busy={loading}
    >
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="space-y-0.5">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Paquete seleccionado</p>
          <h3 id="wishlist-package-heading" className="text-base font-semibold tracking-tight text-primary">
            {selectedCount === 1 ? "1 juego seleccionado" : `${selectedCount} juegos seleccionados`}
          </h3>
        </div>
        <Button type="button" variant="secondary" className="h-8 px-3 text-xs" onClick={onClear}>
          <X className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          Limpiar selección
        </Button>
      </div>

      {error ? <Alert variant="danger">{error}</Alert> : null}

      <div className="grid gap-3 sm:grid-cols-2">
        <PackageScenarioValue
          label="Todo en oficial"
          currency={currency}
          scenario={
            scenariosReady
              ? {
                  subtotalMinor: preview.officialSubtotalMinor,
                  quoted: preview.officialQuoted,
                  missing: preview.officialMissing
                }
              : null
          }
        />
        <PackageScenarioValue
          label="Todo en keys"
          currency={currency}
          scenario={
            scenariosReady
              ? {
                  subtotalMinor: preview.keyshopSubtotalMinor,
                  quoted: preview.keyshopQuoted,
                  missing: preview.keyshopMissing
                }
              : null
          }
        />
      </div>

      {reconciledAppIds.length > 0 ? (
        <p className="text-xs text-muted">
          {reconciledAppIds.length === 1
            ? "1 juego de la selección ya no está en tu wishlist y salió del cálculo."
            : `${reconciledAppIds.length} juegos de la selección ya no están en tu wishlist y salieron del cálculo.`}
        </p>
      ) : null}

      <p className="text-xs text-muted">
        Son dos escenarios alternativos, no dos parciales de un total: el paquete cuesta una cifra si se
        compra todo en tiendas oficiales, u otra si se compra todo en keys. Nunca la suma de las dos.
      </p>
      <p className="text-xs text-muted">
        {pricedAt
          ? `Precios leídos el ${pricedAt}.`
          : "Todavía no se informó cuándo se leyeron los precios."}
        {" "}Ambos mínimos ya están convertidos a MXN.
      </p>
    </section>
  );
}

function CategoryBadges({ categories }: { readonly categories: readonly import("./_lib/wishlist-contract").WishlistCategory[] }) {
  if (categories.length === 0) return <span className="text-xs text-muted">Sin categoría</span>;
  return <span className="flex flex-wrap gap-1" aria-label="Categorías">{categories.map((category) => <span key={category.id} className="tabler-badge tabler-badge-info">{category.name}</span>)}</span>;
}

const NewCategoryForm = memo(function NewCategoryForm({
  busy,
  onCreate,
  onCancel
}: {
  readonly busy: boolean;
  readonly onCreate: (name: string) => Promise<void>;
  readonly onCancel: () => void;
}) {
  const [draft, setDraft] = useState("");

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const name = draft.trim();
    if (!name || busy) return;
    await onCreate(name);
    setDraft("");
  }

  return (
    <form className="flex flex-wrap items-center gap-2" onSubmit={(event) => { void submit(event); }}>
      <label htmlFor="wishlist-new-category" className="sr-only">Nombre de categoría</label>
      <input id="wishlist-new-category" autoFocus value={draft} onChange={(event) => setDraft(event.target.value)} maxLength={80} required placeholder="Nombre de categoría" className="input-semantic h-8 text-xs" />
      <Button type="submit" className="h-8 px-3 text-xs" loading={busy} loadingText="Creando">Crear</Button>
      <Button type="button" variant="ghost" className="h-8 px-3 text-xs" onClick={onCancel}>Cancelar</Button>
    </form>
  );
});

function CategoryModal({
  item,
  categories,
  disabled,
  onClose,
  onSaved,
  onItemCategoriesChanged,
  onError
}: {
  readonly item: WishlistItem | null;
  readonly categories: readonly import("./_lib/wishlist-contract").WishlistCategory[];
  readonly disabled: boolean;
  readonly onClose: () => void;
  readonly onSaved: () => void | Promise<void>;
  readonly onItemCategoriesChanged: (appId: number, categoryIds: readonly number[]) => void;
  readonly onError: (message: string) => void;
}) {
  const [value, setValue] = useState<number[]>([]);
  const [saving, setSaving] = useState(false);
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!item) return;
    setValue(item.categories.map((category) => category.id));
    closeButtonRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [item, onClose]);

  if (!item) return null;
  const currentItem = item;

  async function save() {
    const appId = currentItem.appId;
    setSaving(true);
    try {
      await replaceWishlistItemCategories(appId, value);
      onItemCategoriesChanged(appId, value);
      await onSaved();
      onClose();
    } catch (cause) {
      onError(cause instanceof Error ? cause.message : "No se pudieron guardar las categorías.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <div className="app-card w-full max-w-md space-y-4 p-5 shadow-xl" role="dialog" aria-modal="true" aria-labelledby="wishlist-category-dialog-title" aria-describedby="wishlist-category-dialog-help">
        <div>
          <h2 id="wishlist-category-dialog-title" className="text-lg font-semibold text-primary">Editar categorías</h2>
          <p className="mt-1 text-sm text-secondary">{currentItem.name}</p>
        </div>
        <label htmlFor="wishlist-category-dialog-select" className="text-sm font-medium text-primary">Categorías</label>
        <select id="wishlist-category-dialog-select" multiple value={value.map(String)} disabled={disabled || saving} onChange={(event) => setValue([...event.target.selectedOptions].map((option) => Number(option.value)))} className="input-semantic min-h-32 w-full" aria-describedby="wishlist-category-dialog-help">
          {categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}
        </select>
        <p id="wishlist-category-dialog-help" className="text-xs text-muted">Ctrl/Cmd permite varias categorías. Deja todo sin seleccionar para quitar todas.</p>
        <div className="flex justify-end gap-2">
          <Button ref={closeButtonRef} type="button" variant="ghost" onClick={onClose} disabled={saving}>Cancelar</Button>
          <Button type="button" onClick={() => void save()} loading={saving} loadingText="Guardando">Guardar</Button>
        </div>
      </div>
    </div>
  );
}

// Etiquetas en español del selector de orden: las mismas claves ordenables que ofrecía la tabla
// (`WISHLIST_SORTS`, el orden lo resuelve el servidor igual que antes).
const WISHLIST_SORT_OPTIONS: readonly { readonly value: WishlistSort; readonly label: string }[] = [
  { value: "priority", label: "Prioridad" },
  { value: "name", label: "Juego" },
  { value: "bestPrice", label: "Mejor precio" },
  { value: "bestDiscount", label: "Mayor descuento" },
  { value: "officialDiscount", label: "% dto. oficial" },
  { value: "keyshopDiscount", label: "% dto. keys" },
  { value: "officialPrice", label: "Mín. oficial" },
  { value: "keyshopPrice", label: "Mín. keys" }
];

// El buscador y el orden sustituyen a la fila de búsqueda y a los encabezados ordenables del grid: leen
// y escriben el mismo estado (`search`/`sorting`), así que el servidor recibe exactamente lo mismo.
function WishlistSearchInput({ value, onChange }: { readonly value: string; readonly onChange: (value: string) => void }) {
  const inputId = useId();

  return (
    <div className="flex items-center">
      <label className="sr-only" htmlFor={inputId}>
        Buscar por nombre o AppID
      </label>
      <div className="relative w-full max-w-xs">
        <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted" aria-hidden="true" />
        <input
          id={inputId}
          type="search"
          value={value}
          onChange={(event) => onChange(event.target.value)}
          placeholder="Buscar por nombre o AppID"
          className="input-semantic h-8 w-full pl-7 pr-7 text-xs"
        />
        {value ? (
          <button
            type="button"
            aria-label="Limpiar búsqueda"
            onClick={() => onChange("")}
            className="absolute right-1 top-1/2 -translate-y-1/2 rounded-sm p-0.5 text-muted hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </button>
        ) : null}
      </div>
    </div>
  );
}

function WishlistSortControl({
  sorting,
  onSortingChange
}: {
  readonly sorting: SortingState;
  readonly onSortingChange: (next: SortingState) => void;
}) {
  const sortId = useId();
  const current = sorting.length > 0 && (WISHLIST_SORTS as readonly string[]).includes(sorting[0]?.id ?? "") ? sorting[0] : undefined;

  return (
    <div className="flex flex-wrap items-center gap-2">
      <label htmlFor={sortId} className="text-xs font-medium text-secondary">
        Ordenar por
      </label>
      <select
        id={sortId}
        value={current?.id ?? ""}
        onChange={(event) => {
          const id = event.target.value;
          onSortingChange(id === "" ? [] : [{ id, desc: current?.id === id ? (current?.desc ?? false) : false }]);
        }}
        className="input-semantic h-8 text-xs"
      >
        <option value="">Sin orden</option>
        {WISHLIST_SORT_OPTIONS.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      <Button
        type="button"
        variant="secondary"
        className="h-8 px-2 text-xs"
        disabled={!current}
        onClick={() => {
          if (current) onSortingChange([{ id: current.id, desc: !current.desc }]);
        }}
        aria-label={current?.desc ? "Orden descendente: cambiar a ascendente" : "Orden ascendente: cambiar a descendente"}
      >
        {current?.desc ? (
          <ArrowDown className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        ) : (
          <ArrowUp className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        )}
        {current?.desc ? "Desc" : "Asc"}
      </Button>
    </div>
  );
}

// Paginador del servidor con la misma forma del grid: selector de filas, anterior/siguiente y
// «Página X de Y». El tamaño persiste en la misma clave de localStorage que usaba la tabla.
function WishlistPager({
  pagination,
  pageCount,
  onPaginationChange
}: {
  readonly pagination: PaginationState;
  readonly pageCount: number;
  readonly onPaginationChange: (next: PaginationState) => void;
}) {
  const pagerButtonClass =
    "btn-secondary-semantic h-7 px-2 text-[11px] disabled:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]";
  const pageSizeId = useId();

  return (
    <div className="flex flex-wrap items-center justify-between gap-2 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] px-2 py-1.5">
      <div className="flex items-center gap-2">
        <label htmlFor={pageSizeId} className="text-[11px] text-muted">
          Filas
        </label>
        <select
          id={pageSizeId}
          value={String(pagination.pageSize)}
          onChange={(event) => onPaginationChange({ pageIndex: 0, pageSize: Number(event.target.value) })}
          className="input-semantic h-7 px-2 text-[11px]"
        >
          {WISHLIST_PAGE_SIZES.map((size) => (
            <option key={size} value={String(size)}>
              {size}
            </option>
          ))}
        </select>
      </div>
      <div className="flex items-center gap-1.5">
        <button
          type="button"
          className={pagerButtonClass}
          onClick={() => onPaginationChange({ ...pagination, pageIndex: Math.max(0, pagination.pageIndex - 1) })}
          disabled={pagination.pageIndex === 0}
        >
          Anterior
        </button>
        <span className="text-[11px] text-muted">
          Página {pagination.pageIndex + 1} de {pageCount}
        </span>
        <button
          type="button"
          className={pagerButtonClass}
          onClick={() => onPaginationChange({ ...pagination, pageIndex: pagination.pageIndex + 1 })}
          disabled={pagination.pageIndex + 1 >= pageCount}
        >
          Siguiente
        </button>
      </div>
    </div>
  );
}

// «Mejor precio» y «Mayor descuento» replican los accessors de las columnas que la tabla mostraba en
// escritorio y las tiles no: el mínimo en MXN de los dos escenarios y el mayor descuento de ambos.
function bestPriceMinor(item: WishlistItem): number | null {
  if (item.bestOfficialMinor === null) return item.bestKeyshopMinor;
  if (item.bestKeyshopMinor === null) return item.bestOfficialMinor;
  return Math.min(item.bestOfficialMinor, item.bestKeyshopMinor);
}

function bestDiscountValue(item: WishlistItem): number | null {
  const official = discountPercent(item.basePriceMinor, item.baseCurrency, item.bestOfficialMinor);
  const keyshop = discountPercent(item.basePriceMinor, item.baseCurrency, item.bestKeyshopMinor);
  if (official === null) return keyshop;
  if (keyshop === null) return official;
  return Math.max(official, keyshop);
}

interface WishlistCardProps {
  readonly item: WishlistItem;
  readonly selected: boolean;
  readonly onToggleSelect: (appId: number, checked: boolean) => void;
  readonly refreshingAppId: number | null;
  readonly rowErrors: Readonly<Record<number, string>>;
  readonly minViableDiscountPercent: number;
  readonly onRefresh: (item: WishlistItem) => void;
  readonly onEditCategories: (item: WishlistItem, trigger: HTMLButtonElement) => void;
}

// Tarjeta compacta: una por juego en todos los anchos, con la cáscara de /discover (`app-card` +
// portada + `deal-price` + «Ver precios»). La cabecera (portada + nombre + categorías) y una sola
// línea de precio (mejor precio + mayor descuento + precio base) quedan siempre visibles; el resto
// (mínimos, scores, fechas por proveedor) vive en un `<details>` nativo colapsado por defecto, sin
// estado JS. Sin quitar ningún dato ni ninguna acción: todo lo que la tarjeta ancha mostraba sigue
// aquí, reordenado.
function WishlistCard({
  item,
  selected,
  onToggleSelect,
  refreshingAppId,
  rowErrors,
  minViableDiscountPercent,
  onRefresh,
  onEditCategories
}: WishlistCardProps) {
  const refreshed = formatDateTime(item.refreshedAt);

  return (
    <li className={cn("app-card space-y-3 p-3", selected && "border-[color:var(--color-accent)]")}>
      <div className="flex gap-3">
        <div className="flex w-5 shrink-0 justify-center">
          <input
            type="checkbox"
            aria-label={`Seleccionar ${item.name}`}
            checked={selected}
            onChange={(event) => onToggleSelect(item.appId, event.target.checked)}
            className="mt-1 h-4 w-4 accent-[var(--color-accent)]"
          />
        </div>
        <div className="min-w-0 flex-1 space-y-3">
          <WishlistThumb src={item.imageUrl} className="h-auto w-full sm:h-auto sm:w-full" />
          <div className="min-w-0">
            <Link
              href={`/games/${item.appId}`}
              className="block text-base font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              {item.name}
            </Link>
            <p className="mt-0.5 text-xs text-muted">
              AppID {item.appId}
              {item.priority !== null ? ` · Prioridad ${item.priority}` : ""}
            </p>
            <CategoryBadges categories={item.categories} />
          </div>
          {item.ownedStores.length > 0 ? <p className="text-xs font-medium text-success">Ya adquirido en: {item.ownedStores.join(", ")}</p> : null}
          <div className="flex flex-wrap items-end justify-between gap-3 border-y border-default py-2">
            <PriceFact label="Mejor precio" amountMinor={bestPriceMinor(item)} currency={MXN} />
            <div className="flex flex-wrap items-end gap-3 text-xs">
              <MetricFact label="Descuento"><DiscountValue value={bestDiscountValue(item)} /></MetricFact>
              <PriceFact label="Precio base Steam" amountMinor={item.basePriceMinor} currency={item.baseCurrency} />
            </div>
          </div>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <Link
              href={`/games/${item.appId}`}
              className="text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              Ver precios
            </Link>
            <span className="text-xs text-muted">Actualizado {refreshed ?? "—"}</span>
          </div>
          <div className="flex flex-wrap items-center gap-2 border-t border-default pt-2">
            <Button type="button" variant="ghost" className="h-7 px-2 text-xs" onClick={(event) => onEditCategories(item, event.currentTarget)}>
              Editar categorías
            </Button>
            <RowRefreshButton item={item} refreshing={refreshingAppId === item.appId} blocked={refreshingAppId !== null && refreshingAppId !== item.appId} onRefresh={onRefresh} />
          </div>
          <details className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] px-2 py-1">
            <summary className="cursor-pointer text-xs font-semibold text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">
              Más detalle
            </summary>
            <div className="grid grid-cols-2 gap-x-3 gap-y-2 pb-1 pt-2">
              <PriceFact label="Mínimo histórico" amountMinor={item.historyLowMinor} currency={item.historyLowCurrency} />
              <PriceFact label="Mín. oficial" amountMinor={item.bestOfficialMinor} currency={MXN} />
              <PriceFact label="Mín. keys" amountMinor={item.bestKeyshopMinor} currency={MXN} />
              <MobileMetrics item={item} minViableDiscountPercent={minViableDiscountPercent} />
            </div>
            <div className="pb-1"><WishlistRowMeta item={item} /></div>
          </details>
          {rowErrors[item.appId] ? <p role="alert" className="text-xs text-danger">{rowErrors[item.appId]}</p> : null}
        </div>
      </div>
    </li>
  );
}

interface WishlistItemsProps {
  readonly items: readonly WishlistItem[];
  readonly categories: readonly import("./_lib/wishlist-contract").WishlistCategory[];
  readonly onCategoriesChanged: (created?: import("./_lib/wishlist-contract").WishlistCategory) => void | Promise<void>;
  readonly onItemCategoriesChanged: (appId: number, categoryIds: readonly number[]) => void;
  readonly onBatchCategoriesChanged: (categoryId: number, appIds: readonly number[], remove: boolean) => void;
  readonly onToast: (message: string, variant: "success" | "error") => void;
  readonly refreshingAppId: number | null;
  readonly rowErrors: Readonly<Record<number, string>>;
  readonly minViableDiscountPercent: number;
  readonly onThresholdCommit: (next: number) => Promise<void>;
  readonly onRefresh: (item: WishlistItem) => void;
  readonly search: string;
  readonly onSearchChange: (value: string) => void;
  readonly pagination: PaginationState;
  readonly onPaginationChange: (next: PaginationState) => void;
  readonly sorting: SortingState;
  readonly onSortingChange: (next: SortingState) => void;
  readonly totalItems: number;
  readonly loading: boolean;
  readonly tableError: string | null;
  readonly draftFilters: WishlistFilters;
  readonly onDraftFiltersChange: (next: WishlistFilters) => void;
  readonly filtersOpen: boolean;
  readonly onFiltersOpenChange: (open: boolean) => void;
  readonly onApplyFilters: () => void;
  readonly onClearFilters: () => void;
}

function WishlistItems({
  items,
  categories,
  onCategoriesChanged,
  onItemCategoriesChanged,
  onBatchCategoriesChanged,
  onToast,
  refreshingAppId,
  rowErrors,
  minViableDiscountPercent,
  onThresholdCommit,
  onRefresh,
  search,
  onSearchChange,
  pagination,
  onPaginationChange,
  sorting,
  onSortingChange,
  totalItems,
  loading,
  tableError,
  draftFilters,
  onDraftFiltersChange,
  filtersOpen,
  onFiltersOpenChange,
  onApplyFilters,
  onClearFilters
}: WishlistItemsProps) {

  const [selectedAppIds, setSelectedAppIds] = useState<ReadonlySet<number>>(() => new Set<number>());
  const [categoryFeedback, setCategoryFeedback] = useState("");
  const [categoryFormOpen, setCategoryFormOpen] = useState(false);
  const [batchCategoryId, setBatchCategoryId] = useState<number | "">("");
  const [categoryBusy, setCategoryBusy] = useState(false);
  const [categoryItem, setCategoryItem] = useState<WishlistItem | null>(null);
  const categoryButtonRef = useRef<HTMLButtonElement>(null);
  const categoryTriggerRef = useRef<HTMLButtonElement>(null);
  const closeCategoryModal = useCallback(() => {
    setCategoryItem(null);
    requestAnimationFrame(() => categoryTriggerRef.current?.focus());
  }, []);
  const [preview, setPreview] = useState<WishlistPackagePreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [reconciledAppIds, setReconciledAppIds] = useState<readonly number[]>([]);
  // Los descartes de peticiones viejas no se pintan: la última selección es la que manda.
  const previewRequestRef = useRef(0);
  const previewAbortRef = useRef<AbortController | null>(null);
  // The API already applied search/category filters to the page; never filter the partial page again.
  const filteredItems = items;
  const rangeStart = totalItems === 0 ? 0 : pagination.pageIndex * pagination.pageSize + 1;
  const rangeEnd = Math.min((pagination.pageIndex + 1) * pagination.pageSize, totalItems);

  // Un juego puede salir de la wishlist entre la selección y el cálculo (el sync lo sacó). La selección se
  // poda contra lo que la lista tiene hoy para que el total no cuente una fila que ya no existe.
  // La selección persiste entre páginas. Solo el preview servidor confirma AppIDs eliminados.

  useEffect(() => {
    const appIds = packageRequestAppIds(selectedAppIds);
    const requestId = previewRequestRef.current + 1;
    previewRequestRef.current = requestId;

    if (appIds.length === 0) {
      previewAbortRef.current?.abort();
      setPreview(null);
      setPreviewError(null);
      setPreviewLoading(false);
      return;
    }

    if (exceedsPackageLimit(selectedAppIds)) {
      setPreview(null);
      setPreviewError(
        `Puedes calcular hasta ${MAX_PACKAGE_APP_IDS} juegos a la vez. Quita algunos de la selección para ver el total del paquete.`
      );
      setPreviewLoading(false);
      return;
    }

    setPreviewLoading(true);
    setPreviewError(null);
    previewAbortRef.current?.abort();
    const controller = new AbortController();
    previewAbortRef.current = controller;

    const timer = setTimeout(() => {
      void (async () => {
        try {
          const previewSignal = controller.signal;
          const result = await previewWishlistPackage(appIds, previewSignal);
          if (previewRequestRef.current !== requestId) return;
          setPreview(result);
          // El servidor dice qué AppIDs no son de tu wishlist: se quitan del set y se avisa, en vez de
          // dejar una selección que suma juegos que el cálculo ignoró.
          if (result.unmatchedAppIds.length > 0) {
            setReconciledAppIds(result.unmatchedAppIds);
            setSelectedAppIds((current) => setAppIds(current, result.unmatchedAppIds, false));
          }
        } catch (cause) {
          if (previewRequestRef.current !== requestId) return;
          setPreview(null);
          setPreviewError(
            cause instanceof Error && cause.message ? cause.message : "No se pudo calcular el paquete."
          );
        } finally {
          if (previewRequestRef.current === requestId) setPreviewLoading(false);
        }
      })();
    }, PACKAGE_DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [selectedAppIds]);

  function clearSelection() {
    // Sube el contador para invalidar cualquier respuesta en vuelo: sin esto, una petición ya enviada
    // podría repintar la barra después de limpiar la selección.
    previewRequestRef.current += 1;
    setSelectedAppIds(new Set<number>());
    setPreview(null);
    setPreviewError(null);
    setPreviewLoading(false);
    setReconciledAppIds([]);
  }

  const createCategory = useCallback(async (name: string) => {
    if (!name || categoryBusy) return;
    setCategoryBusy(true); setCategoryFeedback("");
    try {
      const category = await createWishlistCategory(name);
      setCategoryFormOpen(false);
      // La creación ya devuelve la categoría completa: actualizar el estado local evita repetir GET /api/wishlist,
      // que además vuelve a cargar precios, ownership y todas las asignaciones.
      onCategoriesChanged(category);
      setCategoryFeedback("Categoría creada.");
      onToast("Categoría creada.", "success");
      categoryButtonRef.current?.focus();
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : "No se pudo crear la categoría.";
      setCategoryFeedback(message);
      onToast(message, "error");
    } finally {
      setCategoryBusy(false);
    }
  }, [categoryBusy, onCategoriesChanged, onToast]);

  async function applyBatchCategory(remove: boolean) {
    if (batchCategoryId === "" || selectedAppIds.size === 0 || categoryBusy) return;
    const appIds = [...new Set(selectedAppIds)].slice(0, 200);
    setCategoryBusy(true); setCategoryFeedback("");
    try {
      if (remove) await removeWishlistCategoryItems(batchCategoryId, appIds);
      else await assignWishlistCategory(batchCategoryId, appIds);
      onBatchCategoriesChanged(batchCategoryId, appIds, remove);
      await onCategoriesChanged();
      const message = remove ? "Categoría quitada de selección." : "Categoría asignada a selección.";
      setCategoryFeedback(message);
      onToast(message, "success");
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : "No se pudo actualizar la categoría.";
      setCategoryFeedback(message);
      onToast(message, "error");
    }
    finally { setCategoryBusy(false); }
  }

  // La selección vive en el estado del componente: la identidad es el AppID, así que ordenar,
  // filtrar o paginar no mueve la marca.
  const pageCount = Math.max(1, Math.ceil(totalItems / pagination.pageSize));

  return (
    <section className="app-card space-y-4 p-5" aria-labelledby="wishlist-items-heading">
      <div className="space-y-1">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">Juegos seguidos</p>
        <h2 id="wishlist-items-heading" className="text-xl font-semibold tracking-tight text-primary">En tu wishlist</h2>
        <p className="tabler-badge tabler-badge-muted">{items.length === 0 ? "0 juegos" : `${pagination.pageIndex * pagination.pageSize + 1}-${Math.min((pagination.pageIndex + 1) * pagination.pageSize, totalItems)} de ${totalItems} juegos`}</p>
        {selectedAppIds.size > 0 ? (
          <p className="tabler-badge tabler-badge-info">
            {selectedAppIds.size === 1 ? "1 seleccionado" : `${selectedAppIds.size} seleccionados`}
          </p>
        ) : null}
        <p className="text-xs text-muted">«Mín. oficial» y «Mín. keys» ya están en MXN. El precio base y el mínimo histórico se muestran en la moneda del proveedor, sin convertir.</p>
        <p className="text-xs text-muted">«% dto.» se calcula contra el precio de lista de Steam (precio base) y «Deal» es un score híbrido de 0 a 10: 7 puntos por la escala del descuento frente al mínimo viable y 3 por la cercanía al mínimo histórico.</p>
      </div>
      <div className="overflow-hidden rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)]">
        <button
          type="button"
          className="flex w-full items-center justify-between gap-3 px-3 py-3 text-left transition-colors hover:bg-[var(--color-surface-3)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-[color:var(--color-border-focus)] sm:px-4"
          aria-expanded={filtersOpen}
          aria-controls="wishlist-filters"
          onClick={() => onFiltersOpenChange(!filtersOpen)}
        >
          <span className="flex items-center gap-2">
            <SlidersHorizontal className="h-4 w-4 text-accent" aria-hidden="true" />
            <span className="text-sm font-semibold text-primary">Filtros</span>
          </span>
          <span className="text-xs text-muted">{filtersOpen ? "Ocultar" : "Mostrar"}</span>
        </button>
        {filtersOpen ? (
          <form id="wishlist-filters" className="grid gap-3 border-t border-default p-3 sm:p-4 md:grid-cols-2" onSubmit={(event) => { event.preventDefault(); onApplyFilters(); }} aria-label="Filtros de wishlist">
            <div className="grid gap-2 sm:grid-cols-3">
              <input type="search" value={draftFilters.search} onChange={(event) => onDraftFiltersChange({ ...draftFilters, search: event.target.value })} placeholder="Buscar por nombre o AppID" className="input-semantic h-9 text-xs sm:col-span-3" />
              <input type="number" min="0" inputMode="numeric" value={draftFilters.minPrice} onChange={(event) => onDraftFiltersChange({ ...draftFilters, minPrice: event.target.value })} placeholder="Precio mínimo MXN" className="input-semantic h-9 text-xs" />
              <input type="number" min="0" inputMode="numeric" value={draftFilters.maxPrice} onChange={(event) => onDraftFiltersChange({ ...draftFilters, maxPrice: event.target.value })} placeholder="Precio máximo MXN" className="input-semantic h-9 text-xs" />
            </div>
            <div className="grid gap-2 sm:grid-cols-2">
              <select value={draftFilters.owned} onChange={(event) => onDraftFiltersChange({ ...draftFilters, owned: event.target.value as TriState })} className="input-semantic h-9 text-xs"><option value="all">Compra: todas</option><option value="yes">Compra: sí</option><option value="no">Compra: no</option></select>
              <select value={draftFilters.subscription} onChange={(event) => onDraftFiltersChange({ ...draftFilters, subscription: event.target.value as TriState })} className="input-semantic h-9 text-xs"><option value="all">Suscripción: todas</option><option value="yes">Suscripción: sí</option><option value="no">Suscripción: no</option></select>
              <select multiple value={draftFilters.categoryIds.map(String)} onChange={(event) => onDraftFiltersChange({ ...draftFilters, categoryIds: [...event.target.selectedOptions].map((option) => Number(option.value)) })} className="input-semantic min-h-20 text-xs" aria-label="Categorías"><option value="" disabled>Categorías (OR)</option>{categories.map((category) => <option key={category.id} value={category.id}>{category.name} ({category.itemCount})</option>)}</select>
              <label className="flex items-center gap-2 text-xs text-secondary"><input type="checkbox" checked={draftFilters.uncategorized} onChange={(event) => onDraftFiltersChange({ ...draftFilters, uncategorized: event.target.checked })} />Sin categoría</label>
            </div>
            <div className="flex flex-wrap justify-end gap-2 md:col-span-2"><Button type="button" variant="secondary" onClick={onClearFilters}>Limpiar filtros</Button><Button type="submit">Aplicar filtros</Button></div>
          </form>
        ) : null}
      </div>
      <div className="flex flex-wrap items-center gap-2" aria-label="Categorías">
        <Button ref={categoryButtonRef} type="button" variant="secondary" className="h-8 px-3 text-xs" onClick={() => setCategoryFormOpen((open) => !open)}>Nueva categoría</Button>
        {categoryFormOpen ? <NewCategoryForm busy={categoryBusy} onCreate={createCategory} onCancel={() => { setCategoryFormOpen(false); categoryButtonRef.current?.focus(); }} /> : null}
        <span className="sr-only" aria-live="polite">{categoryFeedback}</span>
        {selectedAppIds.size > 0 ? <div className="flex flex-wrap items-center gap-2" aria-label="Acciones de categorías para selección">
          <label htmlFor="wishlist-batch-category" className="sr-only">Categoría seleccionada</label>
          <select id="wishlist-batch-category" value={batchCategoryId} onChange={(event) => setBatchCategoryId(event.target.value ? Number(event.target.value) : "")} className="input-semantic h-8 text-xs"><option value="">Selecciona categoría</option>{categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}</select>
          <Button type="button" variant="secondary" className="h-8 px-3 text-xs" disabled={categoryBusy || batchCategoryId === ""} onClick={() => void applyBatchCategory(false)}>Asignar a categoría</Button>
          <Button type="button" variant="ghost" className="h-8 px-3 text-xs" disabled={categoryBusy || batchCategoryId === ""} onClick={() => void applyBatchCategory(true)}>Quitar de categoría</Button>
        </div> : null}
      </div>
      <ThresholdControl value={minViableDiscountPercent} onCommit={onThresholdCommit} />
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <WishlistSearchInput value={search} onChange={onSearchChange} />
        <WishlistSortControl sorting={sorting} onSortingChange={onSortingChange} />
        {filteredItems.length > 0 ? (
          <label className="flex items-center gap-2 text-xs text-secondary">
            <input
              type="checkbox"
              aria-label="Seleccionar los juegos de esta página"
              checked={pageSelectionState(selectedAppIds, filteredItems.map((item) => item.appId)) === "all"}
              ref={(node) => {
                if (node) {
                  node.indeterminate =
                    pageSelectionState(selectedAppIds, filteredItems.map((item) => item.appId)) === "some";
                }
              }}
              onChange={(event) =>
                setSelectedAppIds((current) =>
                  setAppIds(
                    current,
                    filteredItems.map((item) => item.appId),
                    event.target.checked
                  )
                )
              }
              className="h-3.5 w-3.5 accent-[var(--color-accent)]"
            />
            Seleccionar página
          </label>
        ) : null}
      </div>
      {loading ? (
        <p className="app-card p-5 text-sm text-muted">Cargando...</p>
      ) : tableError ? (
        <Alert variant="danger">{tableError}</Alert>
      ) : filteredItems.length === 0 ? (
        <div className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
          <p>Ningún juego coincide con los filtros actuales.</p>
          <Button type="button" variant="secondary" className="mt-3 h-8 px-3 text-xs" onClick={onClearFilters}>
            Limpiar filtros
          </Button>
        </div>
      ) : (
        <>
          <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3" aria-label="Juegos en la wishlist">
            {filteredItems.map((item) => (
              <WishlistCard
                key={item.appId}
                item={item}
                selected={selectedAppIds.has(item.appId)}
                onToggleSelect={(appId, checked) =>
                  setSelectedAppIds((current) => toggleAppId(current, appId, checked))
                }
                refreshingAppId={refreshingAppId}
                rowErrors={rowErrors}
                minViableDiscountPercent={minViableDiscountPercent}
                onRefresh={onRefresh}
                onEditCategories={(cardItem, trigger) => {
                  categoryTriggerRef.current = trigger;
                  setCategoryItem(cardItem);
                }}
              />
            ))}
          </ul>
          <p className="text-xs text-muted" aria-live="polite">
            {rangeStart}-{rangeEnd} de {totalItems}
          </p>
          <WishlistPager pagination={pagination} pageCount={pageCount} onPaginationChange={onPaginationChange} />
        </>
      )}
      <CategoryModal item={categoryItem} categories={categories} disabled={categoryBusy} onClose={closeCategoryModal} onSaved={onCategoriesChanged} onItemCategoriesChanged={onItemCategoriesChanged} onError={(message) => { setCategoryFeedback(message); onToast(message, "error"); }} />
      {selectedAppIds.size > 0 ? (
        <div className="sticky bottom-2 z-10">
          <PackageSummaryBar
            selectedCount={selectedAppIds.size}
            preview={preview}
            loading={previewLoading}
            error={previewError}
            reconciledAppIds={reconciledAppIds}
            onClear={clearSelection}
          />
        </div>
      ) : null}
    </section>
  );
}

export function WishlistClient() {
  const { toasts, dismissToast, success, error: toastError } = useToasts();
  const [wishlist, setWishlist] = useState<WishlistResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [tableLoading, setTableLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [tableError, setTableError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncError, setSyncError] = useState<string | null>(null);
  const [report, setReport] = useState<WishlistSyncResponse | null>(null);
  const [refreshingAppId, setRefreshingAppId] = useState<number | null>(null);
  const [rowErrors, setRowErrors] = useState<Readonly<Record<number, string>>>({});
  const router = useRouter();
  const searchParams = useSearchParams();
  // Filters only arrive from an explicit shareable URL; they are never restored from local draft state.
  const initialFilters = useMemo(() => filtersFromUrl(searchParams), [searchParams]);
  const [draftFilters, setDraftFilters] = useState<WishlistFilters>(() => initialFilters);
  const [appliedFilters, setAppliedFilters] = useState<WishlistFilters>(() => initialFilters);
  const [filtersOpen, setFiltersOpen] = useState(() => !hasDefaultFilters(initialFilters));
  const [pagination, setPagination] = useState<PaginationState>(() => ({ pageIndex: Math.max(0, Number(searchParams.get("page") || 1) - 1), pageSize: WISHLIST_PAGE_SIZES.includes(Number(searchParams.get("pageSize"))) ? Number(searchParams.get("pageSize")) : 50 }));
  const [sorting, setSorting] = useState<SortingState>(() => {
    const sort = searchParams.get("sort");
    return (WISHLIST_SORTS as readonly string[]).includes(sort ?? "") ? [{ id: sort as WishlistSort, desc: searchParams.get("direction") === "desc" }] : [];
  });
  const requestRef = useRef(0);
  const hasLoadedRef = useRef(false);

  const wishlistQuery = useMemo<WishlistQuery>(() => ({
    ...queryFilters(appliedFilters),
    page: pagination.pageIndex + 1,
    pageSize: pagination.pageSize,
    sort: (WISHLIST_SORTS as readonly string[]).includes(sorting[0]?.id ?? "") ? sorting[0]?.id as WishlistSort : undefined,
    direction: sorting.length > 0 ? (sorting[0].desc ? "desc" : "asc") : undefined
  }), [appliedFilters, pagination, sorting]);

  const wishlistQueryKey = useMemo(() => JSON.stringify(wishlistQuery), [wishlistQuery]);
  const wishlistQueryRef = useRef(wishlistQuery);
  wishlistQueryRef.current = wishlistQuery;

  const loadWishlist = useCallback(async (query: WishlistQuery, signal?: AbortSignal) => {
    const requestId = ++requestRef.current;
    const initialLoad = !hasLoadedRef.current;
    setTableLoading(true);
    setTableError(null);
    if (initialLoad) {
      setLoading(true);
      setError(null);
    }
    try {
      const result = await getWishlist(query, signal);
      if (requestId === requestRef.current) {
        setWishlist(result);
        hasLoadedRef.current = true;
      }
    } catch (cause) {
      if (requestId === requestRef.current) {
        const message = cause instanceof Error ? cause.message : "No se pudo cargar la wishlist.";
        if (initialLoad) setError(message);
        else setTableError(message);
      }
    } finally {
      if (requestId === requestRef.current) {
        setTableLoading(false);
        if (initialLoad) setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    const totalPages = wishlist?.totalPages ?? 0;
    setPagination((current) => {
      const maxPageIndex = Math.max(0, totalPages - 1);
      return current.pageIndex > maxPageIndex ? { ...current, pageIndex: maxPageIndex } : current;
    });
  }, [wishlist?.totalPages]);

  // El tamaño de página persiste en localStorage (la misma clave que usaba la tabla): si hay un valor
  // guardado válido, manda sobre el inicial, igual que hacía la hidratación del grid.
  useEffect(() => {
    try {
      const persisted = window.localStorage.getItem(PAGE_SIZE_STORAGE_KEY);
      const parsed = Number(persisted);
      if (persisted && WISHLIST_PAGE_SIZES.includes(parsed)) {
        setPagination((current) =>
          current.pageIndex === 0 && current.pageSize === parsed
            ? current
            : { pageIndex: 0, pageSize: parsed }
        );
      }
    } catch {
      // localStorage bloqueado: se queda el tamaño inicial.
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void loadWishlist(wishlistQueryRef.current, controller.signal);
    return () => controller.abort();
  }, [loadWishlist, wishlistQueryKey]);

  const syncUrl = useCallback((query: WishlistQuery) => {
    const params = serializeWishlistQuery(query);
    router.replace(`/wishlist${params.size ? `?${params.toString()}` : ""}`, { scroll: false });
  }, [router]);

  function applyFilters() {
    const next = { ...draftFilters, categoryIds: [...draftFilters.categoryIds] };
    setAppliedFilters(next);
    setPagination((current) => ({ ...current, pageIndex: 0 }));
    setFiltersOpen(false);
    syncUrl({ ...queryFilters(next), page: 1, pageSize: pagination.pageSize, sort: wishlistQuery.sort, direction: wishlistQuery.direction });
  }

  function clearFilters() {
    setDraftFilters(DEFAULT_FILTERS);
    setAppliedFilters(DEFAULT_FILTERS);
    setPagination({ pageIndex: 0, pageSize: 50 });
    setSorting([]);
    setFiltersOpen(false);
    // The empty URL is the canonical all-items state: no implicit page, sort, or filter survives.
    syncUrl({});
  }

  function changeSorting(next: SortingState) {
    const supported = next.filter((sort) => (WISHLIST_SORTS as readonly string[]).includes(sort.id)).slice(0, 1);
    setSorting(supported);
    setPagination((current) => ({ ...current, pageIndex: 0 }));
    syncUrl({ ...queryFilters(appliedFilters), page: 1, pageSize: pagination.pageSize, sort: supported[0]?.id as WishlistQuery["sort"], direction: supported.length ? (supported[0].desc ? "desc" : "asc") : undefined });
  }

  function changePagination(next: PaginationState) {
    setPagination(next);
    try {
      window.localStorage.setItem(PAGE_SIZE_STORAGE_KEY, String(next.pageSize));
    } catch {
      // localStorage lleno o bloqueado: la lista sigue funcionando en memoria.
    }
    syncUrl({ ...queryFilters(appliedFilters), page: next.pageIndex + 1, pageSize: next.pageSize, sort: wishlistQuery.sort, direction: wishlistQuery.direction });
  }


  // Sincronizar la lista no vuelve a `loading`: la lista ya cargada se queda en pantalla mientras corre.
  async function runSync() {
    if (syncing) return;
    setSyncing(true);
    setSyncError(null);
    setReport(null);
    const controller = new AbortController();
    try {
      setReport(await syncWishlist(controller.signal));
      try {
        setWishlist(await getWishlist(wishlistQuery, controller.signal));
      } catch {
        // La sincronización ya terminó: no se borra el reporte, solo se dice que faltó recargar.
        setSyncError("La sincronización terminó, pero no se pudo recargar la lista.");
      }
    } catch (cause) {
      setSyncError(cause instanceof Error ? cause.message : "No se pudo sincronizar la wishlist.");
    } finally {
      controller.abort();
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
      setWishlist(await getWishlist(wishlistQuery));
    } catch {
      setSyncError("Los precios se actualizaron, pero no se pudo recargar la lista. Recarga la página para verlos.");
    } finally {
      setRefreshingAppId(null);
    }
  }

  // El umbral es del backend: se guarda con el PUT y el estado local solo se actualiza con el valor
  // confirmado. Si falla, el error lo muestra el control y el valor vigente no cambia.
  const updateItemCategories = useCallback((appId: number, categoryIds: readonly number[]) => {
    setWishlist((current) => {
      if (!current) return current;
      const selected = new Set(categoryIds);
      const oldItem = current.items.find((item) => item.appId === appId);
      if (!oldItem) return current;
      const oldIds = new Set(oldItem.categories.map((category) => category.id));
      const categories = current.categories.map((category) => ({
        ...category,
        itemCount: category.itemCount + (selected.has(category.id) ? (oldIds.has(category.id) ? 0 : 1) : (oldIds.has(category.id) ? -1 : 0))
      }));
      const items = current.items.map((item) => item.appId === appId ? { ...item, categories: categories.filter((category) => selected.has(category.id)) } : item);
      return { ...current, items, categories };
    });
  }, []);

  const updateBatchCategories = useCallback((categoryId: number, appIds: readonly number[], remove: boolean) => {
    setWishlist((current) => {
      if (!current) return current;
      const ids = new Set(appIds);
      const category = current.categories.find((candidate) => candidate.id === categoryId);
      if (!category) return current;
      let delta = 0;
      const items = current.items.map((item) => {
        if (!ids.has(item.appId)) return item;
        const has = item.categories.some((candidate) => candidate.id === categoryId);
        if (remove ? !has : has) return item;
        delta += remove ? -1 : 1;
        return { ...item, categories: remove ? item.categories.filter((candidate) => candidate.id !== categoryId) : [...item.categories, category].sort((a, b) => a.name.localeCompare(b.name, "es-MX")) };
      });
      return { ...current, items, categories: current.categories.map((candidate) => candidate.id === categoryId ? { ...candidate, itemCount: Math.max(0, candidate.itemCount + delta) } : candidate) };
    });
  }, []);

  async function updateThreshold(next: number) {
    const saved = await updateWishlistPreferences(next);
    setWishlist((current) => (current ? { ...current, minViableDiscountPercent: saved } : current));
  }

  if (loading) return <p className="app-card p-5 text-sm text-muted">Cargando...</p>;

  if (error || !wishlist) {
    return (
      <div className="space-y-3">
        <Alert variant="danger">{error ?? "No se pudo cargar la wishlist."}</Alert>
        <Button type="button" variant="secondary" onClick={() => void loadWishlist(wishlistQuery)}>
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
      <ToastStack toasts={toasts} onDismiss={dismissToast} />
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
              {wishlist.totalItems === 1 ? "1 juego" : `${wishlist.totalItems} juegos`}
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

      {wishlist.state === "ok" && wishlist.totalItems === 0 && hasDefaultFilters(appliedFilters) ? (
        <div className="app-card space-y-1 p-5">
          <p className="text-sm font-semibold text-primary">Tu wishlist de Steam está vacía.</p>
          <p className="text-sm text-muted">
            No hay juegos que seguir todavía. Añade juegos a tu wishlist en Steam y vuelve a sincronizar.
          </p>
        </div>
      ) : null}

      {items.length > 0 || wishlist.totalItems > 0 || !hasDefaultFilters(appliedFilters) ? (
        <WishlistItems
          items={items}
          categories={wishlist.categories}
          onToast={(message, variant) => (variant === "success" ? success(message) : toastError(message))}
          onItemCategoriesChanged={updateItemCategories}
          onBatchCategoriesChanged={updateBatchCategories}
          onCategoriesChanged={(created) => {
            if (created === undefined) return;
            setWishlist((current) => current === null ? current : {
              ...current,
              categories: [...current.categories, created].sort((left, right) => left.name.localeCompare(right.name, "es-MX"))
            });
          }}
          refreshingAppId={refreshingAppId}
          rowErrors={rowErrors}
          minViableDiscountPercent={wishlist.minViableDiscountPercent}
          onThresholdCommit={updateThreshold}
          onRefresh={(item) => void refreshItem(item)}
          search={draftFilters.search}
          onSearchChange={(search) => setDraftFilters((current) => ({ ...current, search }))}
          pagination={pagination}
          onPaginationChange={changePagination}
          sorting={sorting}
          onSortingChange={changeSorting}
          totalItems={wishlist.totalItems}
          loading={tableLoading}
          tableError={tableError}
          draftFilters={draftFilters}
          onDraftFiltersChange={setDraftFilters}
          filtersOpen={filtersOpen}
          onFiltersOpenChange={setFiltersOpen}
          onApplyFilters={applyFilters}
          onClearFilters={clearFilters}
        />
      ) : null}
    </div>
  );
}
