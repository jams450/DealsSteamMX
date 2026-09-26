"use client";

import Link from "next/link";
import { memo, useCallback, useEffect, useId, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import type { ColumnDef, FilterFn, SortingFn } from "@tanstack/react-table";
import { Gamepad2, RefreshCw, X } from "lucide-react";
import { DataGrid } from "@/components/data-grid/data-grid";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { PriceFact, PriceValue, formatMinor } from "@/components/ui/price-value";
import { refreshSteamGame } from "@/app/steam/_lib/steam-api";
import { cn } from "@/lib/ui/cn";
import { ToastStack } from "@/components/feedback/toast-stack";
import { useToasts } from "@/components/feedback/use-toasts";
import { assignWishlistCategory, createWishlistCategory, getWishlist, previewWishlistPackage, removeWishlistCategoryItems, replaceWishlistItemCategories, syncWishlist, updateWishlistPreferences, type WishlistQuery } from "./_lib/wishlist-api";
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
import { SYNC_STORES, latestSyncTime, syncStamp } from "./_lib/wishlist-sync";
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
const COLUMN_VISIBILITY_STORAGE_KEY = "wishlist.columns.v1";
// La prioridad de Steam no se usa, así que nace oculta; sigue disponible en el menú «Columnas».
const INITIAL_COLUMN_VISIBILITY = { priority: false };

// El normalizador ya descarta fechas inválidas; el guard evita que Intl.format lance si algo se cuela.
function formatDateTime(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormatter.format(date);
}

// El formateo de importes (`formatMinor`, `PriceValue`, `PriceFact`) vive en
// `components/ui/price-value.tsx` y lo comparten esta página y la biblioteca.

// La prioridad de Steam sigue en la tabla (por si algún día se usa) pero nace oculta: está disponible
// en el menú «Columnas» y no aparece en la línea meta de las tiles móviles.
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

// Las tiles móviles muestran los mismos cuatro valores que la tabla de escritorio.
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

interface WishlistDataGridProps {
  readonly columns: ColumnDef<WishlistItem>[];
  readonly rows: readonly WishlistItem[];
  readonly minViableDiscountPercent: number;
  readonly onThresholdCommit: (next: number) => Promise<void>;
  readonly pagination: PaginationState;
  readonly onPaginationChange: (next: PaginationState) => void;
  readonly sorting: SortingState;
  readonly onSortingChange: (next: SortingState) => void;
  readonly search: string;
  readonly onSearchChange: (value: string) => void;
  readonly rowCount: number;
  readonly loading: boolean;
  readonly error: string | null;
}

function wishlistFilter(row: Parameters<FilterFn<WishlistItem>>[0], _columnId: string, value: unknown) {
  const text = String(value).trim().toLocaleLowerCase("es-MX");
  return row.original.name.toLocaleLowerCase("es-MX").includes(text) || String(row.original.appId).includes(text);
}

// Keep DataGrid outside WishlistItems so category form/modal/feedback state does not render
// its rows. Props are deliberately narrow: only table data or table-owned controls can invalidate it.
const WishlistDataGrid = memo(function WishlistDataGrid({
  columns,
  rows,
  minViableDiscountPercent,
  onThresholdCommit,
  pagination,
  onPaginationChange,
  sorting,
  onSortingChange,
  search,
  onSearchChange,
  rowCount,
  loading,
  error
}: WishlistDataGridProps) {
  return (
    <DataGrid
      columns={columns}
      rows={rows}
      mode="server"
      loading={loading}
      errorMessage={error}
      manualPagination
      pagination={pagination}
      onPaginationChange={onPaginationChange}
      rowCount={rowCount}
      manualSorting
      sorting={sorting}
      onSortingChange={onSortingChange}
      density="compact"
      stickyHeader
      stickyActionsColumn
      pageSizeOptions={WISHLIST_PAGE_SIZES}
      allowAllPageSize
      pageSizeStorageKey={PAGE_SIZE_STORAGE_KEY}
      enableGlobalFilter
      globalFilter={search}
      onGlobalFilterChange={onSearchChange}
      globalFilterPlaceholder="Buscar por nombre o AppID"
      globalFilterFn={wishlistFilter}
      enableColumnVisibility
      columnVisibilityStorageKey={COLUMN_VISIBILITY_STORAGE_KEY}
      initialColumnVisibility={INITIAL_COLUMN_VISIBILITY}
      enableColumnFilters
      toolbar={<ThresholdControl value={minViableDiscountPercent} onCommit={onThresholdCommit} />}
      emptyMessage="Ningún juego coincide con la búsqueda."
    />
  );
});

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
  readonly categoryFilter: number | "all" | "none";
  readonly onCategoryChange: (value: number | "all" | "none") => void;
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
  categoryFilter,
  onCategoryChange,
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

    const timer = setTimeout(() => {
      void (async () => {
        try {
          const result = await previewWishlistPackage(appIds);
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

    return () => clearTimeout(timer);
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

  // Un valor ausente se expresa como `undefined`, nunca `null`, y cada columna ordenable lleva
  // `sortUndefined: "last"`. El paquete resuelve ese caso con un `return` temprano ANTES de invertir
  // por dirección, así que los juegos sin precio quedan al final tanto en asc como en desc. El default
  // (`sortUndefined: 1`) sí se invierte: con él, ordenar descendente pone arriba los juegos sin precio
  // como si fueran los más caros. Un comparador propio no puede arreglarlo; recibe la columna, no la
  // dirección, y su resultado se invierte igual. `0` se conserva: un juego gratis ordena como el menor.
  const numericSort: SortingFn<WishlistItem> = (rowA, rowB, columnId) =>
    Number(rowA.getValue(columnId)) - Number(rowB.getValue(columnId));

  const columns = useMemo<ColumnDef<WishlistItem>[]>(() => {
    return [
    {
      // La selección vive en la columna y no en el DataGrid: el grid es compartido con /users y no necesita
      // saber de paquetes. La identidad es el AppID, así que ordenar o filtrar no mueve la marca.
      id: "select",
      enableSorting: false,
      enableHiding: false,
      header: ({ table }) => {
        const pageAppIds = table.getRowModel().rows.map((row) => row.original.appId);
        const state = pageSelectionState(selectedAppIds, pageAppIds);
        return (
          <input
            type="checkbox"
            aria-label="Seleccionar los juegos de esta página"
            checked={state === "all"}
            ref={(node) => {
              if (node) node.indeterminate = state === "some";
            }}
            disabled={pageAppIds.length === 0}
            onChange={(event) =>
              setSelectedAppIds((current) => setAppIds(current, pageAppIds, event.target.checked))
            }
            className="h-3.5 w-3.5 accent-[var(--color-accent)]"
          />
        );
      },
      cell: ({ row }) => (
        <input
          type="checkbox"
          aria-label={`Seleccionar ${row.original.name}`}
          checked={selectedAppIds.has(row.original.appId)}
          onChange={(event) =>
            setSelectedAppIds((current) => toggleAppId(current, row.original.appId, event.target.checked))
          }
          className="h-3.5 w-3.5 accent-[var(--color-accent)]"
        />
      )
    },
    { id: "cover", header: "Portada", enableSorting: false, cell: ({ row }) => <WishlistThumb src={row.original.imageUrl} /> },
    {
      accessorKey: "name", header: "Juego", sortingFn: (rowA, rowB, id) => String(rowA.getValue(id)).localeCompare(String(rowB.getValue(id)), "es-MX"),
      cell: ({ row }) => <div className="min-w-48"><Link href={`/games/${row.original.appId}`} className="text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">{row.original.name}</Link><p className="text-xs text-muted">AppID {row.original.appId}</p><CategoryBadges categories={row.original.categories} /><Button type="button" variant="ghost" className="mt-1 h-7 px-2 text-xs" onClick={(event) => { categoryTriggerRef.current = event.currentTarget; setCategoryItem(row.original); }}>Editar categorías</Button>{row.original.ownedStores.length > 0 ? <p className="text-xs font-medium text-success">Ya adquirido en: {row.original.ownedStores.join(", ")}</p> : null}{rowErrors[row.original.appId] ? <p role="alert" className="mt-1 text-xs text-danger">{rowErrors[row.original.appId]}</p> : null}</div>
    },
    { id: "priority", accessorFn: (item) => item.priority ?? undefined, header: "Prioridad", sortingFn: numericSort, sortUndefined: "last" },
    { id: "addedAt", accessorFn: (item) => item.addedAt ? new Date(item.addedAt).getTime() : undefined, header: "Alta", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => formatDateTime(row.original.addedAt) ?? "—" },
    { id: "refreshedAt", accessorFn: (item) => item.refreshedAt ? new Date(item.refreshedAt).getTime() : undefined, header: "Actualizado", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => formatDateTime(row.original.refreshedAt) ?? "—" },
    // Ordena por el sello más reciente de los cinco, que es el que responde «¿cuán al día está esta fila?».
    // Los juegos sin ningún sello salen `undefined` y quedan al final en las dos direcciones.
    {
      id: "sync",
      accessorFn: latestSyncTime,
      header: () => (
        <div className="space-y-0.5">
          <span className="block">Sincronización</span>
          <span className="grid grid-cols-5 gap-x-1 text-center text-[0.625rem] font-normal leading-3 text-muted" aria-label="St Steam, IT ITAD, GG GG.deals, Ep Epic, MS Microsoft">
            <span>St</span><span>IT</span><span>GG</span><span>Ep</span><span>MS</span>
          </span>
        </div>
      ),
      enableSorting: true,
      sortingFn: numericSort,
      sortUndefined: "last",
      cell: ({ row }) => <SyncBadges item={row.original} />
    },
    { id: "basePriceMinor", accessorFn: (item) => item.basePriceMinor ?? undefined, header: "Precio base", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.basePriceMinor} currency={row.original.baseCurrency} /> },
    {
      id: "discountOfficial",
      accessorFn: (item) => discountPercent(item.basePriceMinor, item.baseCurrency, item.bestOfficialMinor) ?? undefined,
      header: "% dto. oficial",
      sortingFn: numericSort,
      sortUndefined: "last",
      cell: ({ row }) => <DiscountValue value={row.getValue<number | undefined>("discountOfficial") ?? null} />
    },
    {
      id: "discountKeyshop",
      accessorFn: (item) => discountPercent(item.basePriceMinor, item.baseCurrency, item.bestKeyshopMinor) ?? undefined,
      header: "% dto. keys",
      sortingFn: numericSort,
      sortUndefined: "last",
      cell: ({ row }) => <DiscountValue value={row.getValue<number | undefined>("discountKeyshop") ?? null} />
    },
    {
      id: "dealOfficial",
      accessorFn: (item) => itemScore(item, item.bestOfficialMinor, minViableDiscountPercent) ?? undefined,
      header: "Deal oficial",
      sortingFn: numericSort,
      sortUndefined: "last",
      cell: ({ row }) => <ScoreValue score={row.getValue<number | undefined>("dealOfficial") ?? null} />
    },
    {
      id: "dealKeyshop",
      accessorFn: (item) => itemScore(item, item.bestKeyshopMinor, minViableDiscountPercent) ?? undefined,
      header: "Deal keys",
      sortingFn: numericSort,
      sortUndefined: "last",
      cell: ({ row }) => <ScoreValue score={row.getValue<number | undefined>("dealKeyshop") ?? null} />
    },
    { id: "historyLowMinor", accessorFn: (item) => item.historyLowMinor ?? undefined, header: "Mínimo histórico", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.historyLowMinor} currency={row.original.historyLowCurrency} /> },
    { id: "bestOfficialMinor", accessorFn: (item) => item.bestOfficialMinor ?? undefined, header: "Mín. oficial", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.bestOfficialMinor} currency={MXN} /> },
    { id: "bestKeyshopMinor", accessorFn: (item) => item.bestKeyshopMinor ?? undefined, header: "Mín. keys", sortingFn: numericSort, sortUndefined: "last", cell: ({ row }) => <PriceValue amountMinor={row.original.bestKeyshopMinor} currency={MXN} /> },
    { id: "actions", header: "Acciones", enableSorting: false, cell: ({ row }) => <RowRefreshButton item={row.original} refreshing={refreshingAppId === row.original.appId} blocked={refreshingAppId !== null && refreshingAppId !== row.original.appId} onRefresh={onRefresh} /> }
    ];
  }, [minViableDiscountPercent, onRefresh, refreshingAppId, rowErrors, selectedAppIds]);

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
      <div className="flex flex-wrap items-center gap-2" aria-label="Filtro de categorías">
        <label htmlFor="wishlist-category-filter" className="text-xs font-medium text-secondary">Categoría</label>
        <select id="wishlist-category-filter" value={String(categoryFilter)} onChange={(event) => onCategoryChange(event.target.value === "all" || event.target.value === "none" ? event.target.value : Number(event.target.value))} className="input-semantic h-8 text-xs">
          <option value="all">Todas</option>
          <option value="none">Sin categoría</option>
          {categories.map((category) => <option key={category.id} value={category.id}>{category.name} ({category.itemCount})</option>)}
        </select>
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
      <div className="md:hidden">
        <label className="sr-only" htmlFor="wishlist-filter-mobile">Buscar por nombre o AppID</label>
        <input id="wishlist-filter-mobile" type="search" value={search} onChange={(event) => onSearchChange(event.target.value)} placeholder="Buscar por nombre o AppID" className="input-semantic h-8 w-full text-xs" />
      </div>
      <ul className="space-y-3 md:hidden">
        {filteredItems.map((item) => (
          <li key={item.appId} className={cn("rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3", selectedAppIds.has(item.appId) && "border-[color:var(--color-accent)]")}>
            <div className="flex items-start gap-2"><input
                type="checkbox"
                aria-label={`Seleccionar ${item.name}`}
                checked={selectedAppIds.has(item.appId)}
                onChange={(event) => setSelectedAppIds((current) => toggleAppId(current, item.appId, event.target.checked))}
                className="mt-1 h-4 w-4 shrink-0 accent-[var(--color-accent)]"
              /><WishlistThumb src={item.imageUrl} /><div className="min-w-0"><Link href={`/games/${item.appId}`} className="text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">{item.name}</Link><p className="text-xs text-muted">AppID {item.appId}</p><CategoryBadges categories={item.categories} /></div></div>
            <div className="mt-3 grid grid-cols-2 gap-x-3 gap-y-2"><PriceFact label="Precio base" amountMinor={item.basePriceMinor} currency={item.baseCurrency} /><PriceFact label="Mínimo histórico" amountMinor={item.historyLowMinor} currency={item.historyLowCurrency} /><PriceFact label="Mín. oficial" amountMinor={item.bestOfficialMinor} currency={MXN} /><PriceFact label="Mín. keys" amountMinor={item.bestKeyshopMinor} currency={MXN} /><MobileMetrics item={item} minViableDiscountPercent={minViableDiscountPercent} /></div>
            <div className="mt-3"><WishlistRowMeta item={item} /></div><Button type="button" variant="ghost" className="mt-2 h-7 px-2 text-xs" onClick={(event) => { categoryTriggerRef.current = event.currentTarget; setCategoryItem(item); }}>Editar categorías</Button>
            <div className="mt-3"><RowRefreshButton item={item} refreshing={refreshingAppId === item.appId} blocked={refreshingAppId !== null && refreshingAppId !== item.appId} onRefresh={onRefresh} /></div>
            {rowErrors[item.appId] ? <p role="alert" className="mt-2 text-xs text-danger">{rowErrors[item.appId]}</p> : null}
          </li>
        ))}
      </ul>
      {filteredItems.length === 0 ? (
        <div className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
          <p>{items.length === 0 ? "La wishlist está vacía." : "Ningún juego coincide con los filtros actuales."}</p>
          {items.length === 0 && (search.trim() || categoryFilter !== "all") ? (
            <Button type="button" variant="secondary" className="mt-3 h-8 px-3 text-xs" onClick={onClearFilters}>
              Limpiar filtros
            </Button>
          ) : null}
        </div>
      ) : null}
      <p className="text-xs text-muted" aria-live="polite">
        {rangeStart}-{rangeEnd} de {totalItems}
      </p>
      <WishlistDataGrid
        columns={columns}
        rows={filteredItems}
        minViableDiscountPercent={minViableDiscountPercent}
        onThresholdCommit={onThresholdCommit}
        pagination={pagination}
        onPaginationChange={onPaginationChange}
        sorting={sorting}
        onSortingChange={onSortingChange}
        search={search}
        onSearchChange={onSearchChange}
        rowCount={totalItems}
        loading={loading}
        error={tableError ?? null}
      />
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
  const [search, setSearch] = useState("");
  const [categoryFilter, setCategoryFilter] = useState<number | "all" | "none">("all");
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 50 });
  const [sorting, setSorting] = useState<SortingState>([]);
  const requestRef = useRef(0);
  const hasLoadedRef = useRef(false);

  const wishlistQuery = useMemo<WishlistQuery>(() => ({
    page: pagination.pageIndex + 1,
    pageSize: pagination.pageSize,
    search: search.trim() || undefined,
    categoryId: typeof categoryFilter === "number" ? categoryFilter : undefined,
    categoryState: categoryFilter === "none" ? "none" : "all",
    sort: sorting[0]?.id === "name" || sorting[0]?.id === "priority" || sorting[0]?.id === "addedAt" ? sorting[0].id : undefined,
    direction: sorting[0]?.desc ? "desc" : sorting.length > 0 ? "asc" : undefined
  }), [categoryFilter, pagination, search, sorting]);

  const loadWishlist = useCallback(async (query: WishlistQuery = wishlistQuery, signal?: AbortSignal) => {
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
  }, [wishlistQuery]);

  useEffect(() => {
    const controller = new AbortController();
    const timer = setTimeout(() => void loadWishlist(wishlistQuery, controller.signal), search.trim() ? 300 : 0);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [loadWishlist, wishlistQuery, search]);

  function changeSearch(value: string) {
    setSearch(value);
    setPagination((current) => ({ ...current, pageIndex: 0 }));
  }

  function changeCategory(value: number | "all" | "none") {
    setCategoryFilter(value);
    setPagination((current) => ({ ...current, pageIndex: 0 }));
  }

  function changeSorting(next: SortingState) {
    setSorting(next);
    setPagination((current) => ({ ...current, pageIndex: 0 }));
  }


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

      {items.length > 0 || search.trim() || categoryFilter !== "all" ? (
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
          search={search}
          onSearchChange={changeSearch}
          pagination={pagination}
          onPaginationChange={setPagination}
          sorting={sorting}
          onSortingChange={changeSorting}
          totalItems={wishlist.totalItems}
          loading={tableLoading}
          tableError={tableError}
          categoryFilter={categoryFilter}
          onCategoryChange={changeCategory}
          onClearFilters={() => {
            setSearch("");
            setCategoryFilter("all");
            setPagination((current) => ({ ...current, pageIndex: 0 }));
          }}
        />
      ) : null}
    </div>
  );
}
