"use client";

import { useCallback, useEffect, useId, useMemo, useRef, useState, type ChangeEvent, type KeyboardEvent as ReactKeyboardEvent } from "react";
import Link from "next/link";
import type { PaginationState, SortingState, VisibilityState } from "@tanstack/react-table";
import { ArrowDown, ArrowUp, Columns3, FileJson, Gamepad2, GitMerge, HardDriveDownload, Search, Star, Trophy, Upload, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { cn } from "@/lib/ui/cn";
import { normalizeStore, storeLabel } from "@/lib/contracts/stores";
import { REVIEW_STATUSES, reviewStatusLabel, type Review, type ReviewStatus } from "@/lib/contracts/reviews";
import { setFavorite } from "@/lib/api/favorites";
import { getLibrary, importLibrary, syncLibraryCovers } from "./_lib/library-api";
import { defaultReviewPlatform, ReviewDrawer, stateLabel } from "./_components/review-drawer";
import { ManualAddDialog } from "./_components/manual-add-dialog";
import { ConsoleImportDialog } from "./_components/console-import-dialog";
import { CoverPicker } from "./_components/cover-picker";
import { TitleEditor } from "./_components/title-editor";
import type { LibraryCoverSyncReport } from "@/lib/contracts/library-covers";
import {
  groupLibraryItems,
  LIBRARY_IMPORT_MAX_BYTES,
  type LibraryGame,
  type LibraryImportReport,
  type LibraryResponse,
  type LibraryState,
  type LibraryStoreCount
} from "./_lib/library-contract";

function readImportFile(file: File): Promise<unknown[]> {
  if (file.size > LIBRARY_IMPORT_MAX_BYTES) {
    return Promise.reject(new Error("El archivo supera el límite de 10 MiB."));
  }

  return file.text().then((text) => {
    if (new TextEncoder().encode(text).length > LIBRARY_IMPORT_MAX_BYTES) {
      throw new Error("El archivo supera el límite de 10 MiB.");
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      throw new Error("El archivo no es JSON válido.");
    }

    if (!Array.isArray(parsed)) {
      throw new Error("El JSON debe tener un arreglo en la raíz, como el export de Playnite.");
    }
    if (parsed.length === 0) {
      throw new Error("El arreglo está vacío: no hay juegos que importar.");
    }

    return parsed;
  });
}

// 120x45 es el tamaño nativo de las portadas de Steam; 90x34 en móvil para que la fila siga cabiendo a
// 360px. La portada es decorativa (`alt=""`) porque el nombre del juego va en su propia columna.
function LibraryThumb({ src }: { readonly src: string | null }) {
  const [failed, setFailed] = useState(false);
  const image = src && !failed ? src : null;

  return (
    <span className="inline-flex h-[34px] w-[90px] shrink-0 items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] sm:h-[45px] sm:w-[120px]">
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

// Game Pass no es posesión: el tag es sólido y el más visible de la fila. El texto sale de `stateLabel`
// para que la grilla y el drawer digan exactamente lo mismo.
// El tono es el verde de Xbox (`tabler-badge-xbox`), no `success`: GOTY ya usa el verde semántico y dos
// verdes iguales en la misma fila no distinguirían «suscripción» de «premio».
//
// Los tres estados forman una escala de ruido, no tres colores sueltos: Game Pass es el caso especial y
// lleva el tono sólido (el más fuerte); «wished» es intención y lleva info; «owned» es el estado NORMAL
// de una biblioteca y lleva el azul de acento, que es el color de estructura de la página. Si el caso
// mayoritario (owned) fuese el más ruidoso, la fila gritaría en cada línea y lo especial dejaría de
// distinguirse.
function StateBadge({ state }: { readonly state: LibraryState }) {
  if (state === "subscription") {
    return <span className="tabler-badge tabler-badge-solid tabler-badge-xbox">{stateLabel(state)}</span>;
  }
  return state === "wished" ? (
    <span className="tabler-badge tabler-badge-info">{stateLabel(state)}</span>
  ) : (
    <span className="tabler-badge tabler-badge-primary">{stateLabel(state)}</span>
  );
}

function StoreBadge({ store }: { readonly store: string }) {
  return <span className="text-[10px] font-semibold uppercase tracking-widest text-muted">{storeLabel(store)}</span>;
}

// Orden visual de los estados cuando un grupo mezcla varios. No se elige uno "principal" y se esconden
// los demás: un juego comprado en Steam y además en Game Pass muestra los dos tags, con la suscripción
// primero, para que la fila no afirme una compra donde solo hay suscripción (ni al revés). El grupo ya
// trae estados únicos; esta lista solo decide el orden.
const STATE_PRECEDENCE: readonly LibraryState[] = ["subscription", "owned", "wished"];

function StateCell({ game }: { readonly game: LibraryGame }) {
  const states = STATE_PRECEDENCE.filter((state) => game.states.includes(state));

  return (
    <div className="flex flex-wrap items-center gap-1">
      {states.map((state) => (
        <StateBadge key={state} state={state} />
      ))}
      {game.isInstalled ? (
        <span className="tabler-badge tabler-badge-info">
          <HardDriveDownload className="h-3 w-3" aria-hidden="true" />
          Instalado
        </span>
      ) : null}
    </div>
  );
}

// Reseña de la última reseña del grupo, en solo lectura. La etiqueta de la nota llega del servidor:
// aquí no se calcula ni se replica ningún rango.
function ReviewBadges({ review }: { readonly review: Review }) {
  return (
    <div className="flex flex-wrap items-center gap-1">
      {review.score !== null ? (
        <span className="tabler-badge tabler-badge-info">
          Nota {review.score}
          {review.scoreLabel ? ` · ${review.scoreLabel}` : ""}
        </span>
      ) : (
        <span className="tabler-badge tabler-badge-neutral">Sin nota</span>
      )}
      {review.isGoty ? (
        <span className="tabler-badge tabler-badge-success">
          <Trophy className="h-3 w-3" aria-hidden="true" />
          GOTY
        </span>
      ) : null}
    </div>
  );
}

// Acción de reseñar de una fila. Si el juego no se puede reseñar (sin identidad en el catálogo o sin
// ninguna tienda del vocabulario) NO se ofrece un botón que falle: se escribe el motivo en su lugar.
function ReviewAction({ game, onReview }: { readonly game: LibraryGame; readonly onReview: (game: LibraryGame) => void }) {
  if (defaultReviewPlatform(game) === null) {
    return (
      <span className="text-[11px] text-muted">
        {game.gameId === null ? "Sin identificar en el catálogo" : "Tienda fuera del vocabulario"}
      </span>
    );
  }

  // Con reseñas guardadas el botón abre la lista (editar una, borrar otra, agregar una nueva); sin
  // ninguna abre el mismo drawer en modo alta.
  const label = game.hasReview ? "Reseñas" : "Reseñar";
  return (
    <Button
      type="button"
      variant="secondary"
      className="h-9 whitespace-nowrap px-3 text-xs"
      aria-label={`${label}: ${game.title}`}
      onClick={() => onReview(game)}
    >
      {label}
    </Button>
  );
}

function ImportReport({ report }: { readonly report: LibraryImportReport }) {
  return (
    <div className="space-y-2" aria-live="polite">
      <p className="text-sm font-semibold text-primary">Resultado de la importación</p>
      <div className="flex flex-wrap items-center gap-2">
        <span className="tabler-badge tabler-badge-info">Importados {report.imported}</span>
        <span className="tabler-badge tabler-badge-info">Actualizados {report.updated}</span>
        <span className={cn("tabler-badge", report.unresolved > 0 ? "tabler-badge-warning" : "tabler-badge-muted")}>
          Sin resolver {report.unresolved}
        </span>
        <span className={cn("tabler-badge", report.unsupportedSource > 0 ? "tabler-badge-danger" : "tabler-badge-muted")}>
          Fuente no soportada {report.unsupportedSource}
        </span>
      </div>
      {report.byStore.length > 0 ? (
        <ul className="flex flex-wrap items-center gap-2">
          {report.byStore.map((entry) => (
            <li key={entry.store} className="tabler-badge tabler-badge-muted">
              {storeLabel(entry.store)} {entry.count}
            </li>
          ))}
        </ul>
      ) : null}
      <p className="text-xs text-muted">
        «Importados» y «Actualizados» cuentan las filas escritas. «Sin resolver» son entradas guardadas a las
        que todavía no se les asignó identidad en el catálogo del comparador. «Fuente no soportada» son filas
        con una tienda fuera del contrato del export. Reimportar no duplica ni borra nada.
      </p>
    </div>
  );
}

// Búsqueda sin acentos ni mayúsculas: el export de Playnite trae títulos en varios idiomas y quien busca
// no tiene por qué escribir la tilde exacta («pokemon» debe encontrar «Pokémon»). Se descompone el
// carácter (NFD) y se quitan los diacríticos combinantes; no hay forma más corta en ES2017.
function fold(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase("es-MX");
}

// Buscador global: por título y por tienda, sobre el juego agrupado, no sobre una celda.
function matchesLibrarySearch(game: LibraryGame, value: string): boolean {
  const query = fold(String(value).trim());
  if (query === "") return true;

  if (fold(game.title).includes(query)) return true;
  return game.stores.some((store) => fold(storeLabel(store)).includes(query));
}

// Ordenables de la lista: las mismas columnas que la tabla permitía ordenar (título, estado de juego,
// última reseña y años jugados). Un valor ausente queda al final en las dos direcciones, igual que el
// `sortUndefined: "last"` que llevaban las columnas.
type LibrarySortId = "title" | "playStatus" | "score" | "playedYears";

const LIBRARY_SORT_OPTIONS: readonly { readonly value: LibrarySortId; readonly label: string }[] = [
  { value: "title", label: "Juego" },
  { value: "playStatus", label: "Estado de juego" },
  { value: "score", label: "Última reseña" },
  { value: "playedYears", label: "Años jugados" }
];

function librarySortValue(game: LibraryGame, sortId: LibrarySortId): string | number | undefined {
  switch (sortId) {
    case "title":
      return game.title;
    case "playStatus":
      return PLAY_STATUS_ORDER[game.playStatus];
    case "score":
      return game.lastReview?.score ?? undefined;
    case "playedYears":
      return game.playedYears[0] ?? undefined;
  }
}

function compareLibraryGames(sortId: LibrarySortId, desc: boolean, left: LibraryGame, right: LibraryGame): number {
  const leftValue = librarySortValue(left, sortId);
  const rightValue = librarySortValue(right, sortId);
  if (leftValue === undefined && rightValue === undefined) return 0;
  if (leftValue === undefined) return 1;
  if (rightValue === undefined) return -1;
  const result =
    typeof leftValue === "string" || typeof rightValue === "string"
      ? String(leftValue).localeCompare(String(rightValue), "es-MX")
      : Number(leftValue) - Number(rightValue);
  return desc ? -result : result;
}

// Filtro de estado de juego del toolbar. Un estado derivado no se resuelve bien con un input de texto
// por columna, así que se resuelve aquí, con el conteo de cada opción para que el filtro sea también
// el reporte ("cuántos por año / por estado").
type PlayStatusFilter = "all" | ReviewStatus | "backlog";

const PLAY_STATUS_FILTERS: readonly { readonly value: PlayStatusFilter; readonly label: string }[] = [
  { value: "all", label: "Todos" },
  { value: "backlog", label: "Por jugar" },
  ...REVIEW_STATUSES.map((option) => ({ value: option.value as PlayStatusFilter, label: option.label }))
];

// `Sin año` es una opción real, no un vacío accidental: agrupa lo que no tiene ninguna reseña con fecha y
// por eso no puede entrar en ningún reporte por año.
export const NO_YEAR = "none";

function FilterToggle<T extends string>({
  id,
  label,
  options,
  value,
  counts,
  totalCount,
  onChange
}: {
  readonly id: string;
  readonly label: string;
  readonly options: readonly { readonly value: T; readonly label: string }[];
  readonly value: T;
  readonly counts: ReadonlyMap<T, number>;
  readonly totalCount: number;
  readonly onChange: (next: T) => void;
}) {
  return (
    <div className="space-y-1.5">
      <p id={`${id}-label`} className="text-sm font-medium text-primary">
        {label}
      </p>
      <div
        className="flex flex-wrap items-center gap-1 border border-strong bg-[var(--color-surface-2)] p-0.5"
        role="group"
        aria-labelledby={`${id}-label`}
      >
        {options.map((option) => {
          const count = option.value === "all" ? totalCount : (counts.get(option.value) ?? 0);
          return (
            <button
              key={option.value}
              type="button"
              aria-pressed={value === option.value}
              onClick={() => onChange(option.value)}
              className={cn(
                "h-9 px-3 text-xs font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                value === option.value
                  ? "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]"
                  : "text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary"
              )}
            >
              {option.label} ({count})
            </button>
          );
        })}
      </div>
    </div>
  );
}

// Acción de portada de una fila. Solo aparece cuando hay algo que hacer: sin identidad canónica no hay
// dónde guardarla (el motivo ya lo dice la acción de reseña de la misma fila) y con portada puesta no hay
// nada que buscar. El selector permite reemplazarla aunque exista, desde el mismo botón.
function CoverAction({ game, onPickCover }: { readonly game: LibraryGame; readonly onPickCover: (game: LibraryGame) => void }) {
  if (game.gameId === null) return null;

  const label = game.imageUrl === null ? "Portada" : "Cambiar portada";
  return (
    <Button
      type="button"
      variant="secondary"
      className="h-9 whitespace-nowrap px-3 text-xs"
      aria-label={`${label}: ${game.title}`}
      onClick={() => onPickCover(game)}
    >
      {label}
    </Button>
  );
}

// Acción de título de una fila. Solo aparece con identidad canónica: sin juego en el catálogo no hay
// nombre compartido que corregir (el texto de una fila suelta es el que importó Playnite, y esta
// herramienta no lo toca). El editor abre encima de la grilla y luego la recarga entera.
function TitleAction({
  game,
  onEditTitle
}: {
  readonly game: LibraryGame;
  readonly onEditTitle: (game: LibraryGame) => void;
}) {
  if (game.gameId === null) return null;

  return (
    <Button
      type="button"
      variant="secondary"
      className="h-9 whitespace-nowrap px-3 text-xs"
      aria-label={`Editar el título del catálogo: ${game.title}`}
      onClick={() => onEditTitle(game)}
    >
      Editar título
    </Button>
  );
}

// Reporte de una pasada de sincronización. Los números describen lo que hizo la pasada, no lo que falta en
// total: «Actualizadas» se abre por fuente, «Sin resultado» son los juegos que ninguna fuente pudo resolver y
// «Fallidas» los que se quedaron sin consultar alguna. Ni unos ni otros abortan la pasada.
function CoverSyncReportBadges({ report }: { readonly report: LibraryCoverSyncReport }) {
  return (
    <div className="space-y-2" aria-live="polite">
      <p className="text-sm font-semibold text-primary">Resultado de la sincronización de portadas</p>
      <div className="flex flex-wrap items-center gap-2">
        <span className="tabler-badge tabler-badge-success">Puestas {report.updated}</span>
        <span className="tabler-badge tabler-badge-info">Steam {report.updatedBySteam}</span>
        <span className="tabler-badge tabler-badge-info">IGDB {report.updatedByIgdb}</span>
        <span className="tabler-badge tabler-badge-info">SteamGridDB {report.updatedBySteamGridDb}</span>
        <span className={cn("tabler-badge", report.failed > 0 ? "tabler-badge-warning" : "tabler-badge-muted")}>
          Fallidas {report.failed}
        </span>
        <span className="tabler-badge tabler-badge-muted">Sin resultado {report.unmatched}</span>
        <span className="tabler-badge tabler-badge-muted">Pendientes de otra pasada {report.remaining}</span>
        <span className="tabler-badge tabler-badge-muted">Sin portada {report.missing}</span>
      </div>
      <p className="text-xs text-muted">
        Cada pasada revisa hasta 25 juegos y prueba las fuentes en cadena —Steam, IGDB y SteamGridDB, en el
        orden que sugieren las tiendas del juego—, y solo rellena portadas que falten: nunca reemplaza una que
        ya exista. «Sin resultado» son los juegos que ninguna fuente pudo resolver; para esos usa «Portada» en
        la fila y elige el resultado a mano. «Fallidas» son los que dejaron alguna fuente sin consultar, y
        «Pendientes de otra pasada» se vacía repitiendo el botón.
      </p>
    </div>
  );
}

// Orden del ciclo de vida para el orden por estado: el índice es el valor que ordena.
const PLAY_STATUS_ORDER: Readonly<Record<ReviewStatus | "backlog", number>> = {
  backlog: 0,
  dropped: 1,
  finished: 2,
  completed: 3
};

// Estrella de favorito de una fila. Es un interruptor propio del juego (no de la reseña), así que vive
// junto a las acciones y no dentro del estado de juego.
function FavoriteToggle({
  game,
  pending,
  onToggle
}: {
  readonly game: LibraryGame;
  readonly pending: boolean;
  readonly onToggle: (game: LibraryGame) => void;
}) {
  if (game.gameId === null) {
    return <span className="text-[11px] text-muted">Sin identificar en el catálogo</span>;
  }

  const label = game.isFavorite ? "Quitar de favoritos" : "Marcar como favorito";
  return (
    <Button
      type="button"
      variant={game.isFavorite ? "primary" : "secondary"}
      className="h-9 whitespace-nowrap px-3 text-xs"
      loading={pending}
      aria-pressed={game.isFavorite}
      aria-label={`${label}: ${game.title}`}
      onClick={() => onToggle(game)}
    >
      <Star className={cn("h-3.5 w-3.5", game.isFavorite && "fill-current")} aria-hidden="true" />
      Favorito
    </Button>
  );
}

// Estado de juego del grupo, en solo lectura. "Por jugar" no es una reseña: es su ausencia.
const PLAY_STATUS_BADGES: Readonly<Record<ReviewStatus | "backlog", string>> = {
  backlog: "tabler-badge tabler-badge-neutral",
  finished: "tabler-badge tabler-badge-success",
  completed: "tabler-badge tabler-badge-solid tabler-badge-primary",
  dropped: "tabler-badge tabler-badge-warning"
};

function PlayStatusBadge({ game }: { readonly game: LibraryGame }) {
  return <span className={PLAY_STATUS_BADGES[game.playStatus]}>{reviewStatusLabel(game.playStatus)}</span>;
}

// Secciones de la tarjeta, con los mismos ids y etiquetas que las columnas de la tabla: la preferencia
// guardada en `library.columns.v1` sigue valiendo porque habla el mismo vocabulario. `actions` no se
// puede ocultar, igual que en el grid.
const LIBRARY_COLUMN_OPTIONS: readonly { readonly id: string; readonly label: string }[] = [
  { id: "cover", label: "Portada" },
  { id: "title", label: "Juego" },
  { id: "stores", label: "Tiendas" },
  { id: "state", label: "Estado" },
  { id: "playStatus", label: "Estado de juego" },
  { id: "favorite", label: "Favorito" },
  { id: "score", label: "Última reseña" },
  { id: "playedYears", label: "Años jugados" }
];

const LIBRARY_PAGE_SIZES = [10, 25, 50, 100];
const LIBRARY_PAGE_SIZE_STORAGE_KEY = "library.pageSize.v1";
const LIBRARY_COLUMN_VISIBILITY_STORAGE_KEY = "library.columns.v1";

function isSectionVisible(visibility: VisibilityState, id: string): boolean {
  return visibility[id] !== false;
}

function LibrarySearchInput({ value, onChange }: { readonly value: string; readonly onChange: (value: string) => void }) {
  const inputId = useId();

  return (
    <div className="flex items-center">
      <label className="sr-only" htmlFor={inputId}>
        Buscar por juego o tienda
      </label>
      <div className="relative w-full max-w-xs">
        <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted" aria-hidden="true" />
        <input
          id={inputId}
          type="search"
          value={value}
          onChange={(event) => onChange(event.target.value)}
          placeholder="Buscar por juego o tienda"
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

function LibrarySortControl({
  sorting,
  onSortingChange
}: {
  readonly sorting: SortingState;
  readonly onSortingChange: (next: SortingState) => void;
}) {
  const sortId = useId();
  const current = sorting.length > 0 ? sorting[0] : undefined;
  const currentId = current !== undefined && LIBRARY_SORT_OPTIONS.some((option) => option.value === current.id)
    ? current.id
    : "";

  return (
    <div className="flex flex-wrap items-center gap-2">
      <label htmlFor={sortId} className="text-xs font-medium text-secondary">
        Ordenar por
      </label>
      <select
        id={sortId}
        value={currentId}
        onChange={(event) => {
          const id = event.target.value;
          onSortingChange(id === "" ? [] : [{ id, desc: current?.id === id ? (current?.desc ?? false) : false }]);
        }}
        className="input-semantic h-8 text-xs"
      >
        <option value="">Sin orden</option>
        {LIBRARY_SORT_OPTIONS.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      <Button
        type="button"
        variant="secondary"
        className="h-8 px-2 text-xs"
        disabled={currentId === ""}
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

// El mismo menú «Columnas» del grid, pero aplicado a las secciones de la tarjeta. Persiste en la misma
// clave y con la misma forma (`{ id: boolean }`), así que lo guardado por la tabla sigue valiendo.
function LibraryColumnsMenu({
  visibility,
  onChange
}: {
  readonly visibility: VisibilityState;
  readonly onChange: (next: VisibilityState) => void;
}) {
  const [open, setOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;

    function handlePointerDown(event: PointerEvent) {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    }

    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [open ]);

  function handleMenuKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key === "Escape") {
      setOpen(false);
      buttonRef.current?.focus();
    }
  }

  return (
    <div className="relative ml-auto" ref={menuRef} onKeyDown={handleMenuKeyDown}>
      <button
        ref={buttonRef}
        type="button"
        aria-haspopup="true"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className="btn-secondary-semantic inline-flex h-7 items-center gap-1 px-2 text-[11px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
      >
        <Columns3 className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        Columnas
      </button>
      {open ? (
        <div
          role="group"
          aria-label="Columnas visibles"
          className="absolute right-0 z-30 mt-1 min-w-40 rounded-[var(--radius-md)] border border-strong bg-[var(--color-surface-1)] p-1 shadow-[var(--shadow-md)]"
        >
          {LIBRARY_COLUMN_OPTIONS.map((option) => (
            <label
              key={option.id}
              className="flex cursor-pointer items-center gap-2 px-2 py-1 text-xs text-secondary hover:text-primary"
            >
              <input
                type="checkbox"
                checked={isSectionVisible(visibility, option.id)}
                onChange={() => onChange({ ...visibility, [option.id]: !isSectionVisible(visibility, option.id) })}
                className="h-3.5 w-3.5 accent-[var(--color-accent)]"
              />
              <span>{option.label}</span>
            </label>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function LibraryPager({
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
          onChange={(event) => onPaginationChange({ ...pagination, pageIndex: 0, pageSize: Number(event.target.value) })}
          className="input-semantic h-7 px-2 text-[11px]"
        >
          {LIBRARY_PAGE_SIZES.map((size) => (
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

interface LibraryCardProps {
  readonly game: LibraryGame;
  readonly visibility: VisibilityState;
  readonly favoritePending: boolean;
  readonly onToggleFavorite: (game: LibraryGame) => void;
  readonly onReview: (game: LibraryGame) => void;
  readonly onPickCover: (game: LibraryGame) => void;
  readonly onEditTitle: (game: LibraryGame) => void;
}

// Una sola tarjeta por juego en todos los anchos, con la cáscara de /discover (`app-card` + portada +
// «Ver precios»): reúne las columnas de la tabla en el orden en que aparecían, sin quitar ningún dato
// ni ninguna acción. «Ver precios» solo existe cuando la fila trae appid de Steam.
function LibraryCard({
  game,
  visibility,
  favoritePending,
  onToggleFavorite,
  onReview,
  onPickCover,
  onEditTitle
}: LibraryCardProps) {
  return (
    <li className="app-card flex items-start gap-3 p-4">
      {isSectionVisible(visibility, "cover") ? <LibraryThumb src={game.imageUrl} /> : null}
      <div className="min-w-0 flex-1 space-y-2">
        {isSectionVisible(visibility, "title") ? (
          <p className="text-sm font-semibold text-primary">{game.title}</p>
        ) : null}
        {isSectionVisible(visibility, "stores") ? (
          <div className="flex flex-wrap items-center gap-2">
            {game.stores.map((store) => (
              <StoreBadge key={store} store={store} />
            ))}
          </div>
        ) : null}
        {isSectionVisible(visibility, "state") ? <StateCell game={game} /> : null}
        {isSectionVisible(visibility, "playStatus") ? <PlayStatusBadge game={game} /> : null}
        {isSectionVisible(visibility, "favorite") ? (
          <div>
            <FavoriteToggle game={game} pending={favoritePending} onToggle={onToggleFavorite} />
          </div>
        ) : null}
        {isSectionVisible(visibility, "score") ? (
          game.lastReview === null ? (
            <span className="text-xs text-muted">—</span>
          ) : (
            <ReviewBadges review={game.lastReview} />
          )
        ) : null}
        {isSectionVisible(visibility, "playedYears") ? (
          game.playedYears.length === 0 ? (
            <span className="text-xs text-muted">—</span>
          ) : (
            <div className="flex flex-wrap items-center gap-1">
              {game.playedYears.map((year) => (
                <span key={year} className="tabler-badge tabler-badge-muted tabular-nums">
                  {year}
                </span>
              ))}
            </div>
          )
        ) : null}
        <div className="flex flex-wrap items-center gap-2">
          {game.item.steamAppId !== null ? (
            <Link
              href={`/games/${game.item.steamAppId}`}
              className="text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              Ver precios
            </Link>
          ) : null}
          <ReviewAction game={game} onReview={onReview} />
          <CoverAction game={game} onPickCover={onPickCover} />
          <TitleAction game={game} onEditTitle={onEditTitle} />
        </div>
      </div>
    </li>
  );
}

export function LibraryClient() {
  const [library, setLibrary] = useState<LibraryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [storeFilter, setStoreFilter] = useState("");
  const [playStatusFilter, setPlayStatusFilter] = useState<PlayStatusFilter>("all");
  const [yearFilter, setYearFilter] = useState<string>("all");
  // Buscador, orden, paginación y columnas visibles: lo que el DataGrid resolvía en el navegador, ahora
  // en estado propio con la misma semántica (mismas claves de localStorage, mismos comparadores).
  const [search, setSearch] = useState("");
  const [sorting, setSorting] = useState<SortingState>([]);
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 10 });
  const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});
  // El favorito es del juego, no de la reseña: se cambia con el mismo `applyFavorite` que se usa para
  // pintar el resultado, así que un juego en dos tiendas se marca en las dos filas a la vez.
  const [favoritePendingId, setFavoritePendingId] = useState<number | null>(null);
  const [favoriteError, setFavoriteError] = useState<string | null>(null);
  const [coverSyncing, setCoverSyncing] = useState(false);
  const [coverReport, setCoverReport] = useState<LibraryCoverSyncReport | null>(null);
  const [coverError, setCoverError] = useState<string | null>(null);
  // Juego cuyo selector de portada está abierto. Solo se abre con identidad canónica, que es donde se
  // guarda la portada.
  const [coverGame, setCoverGame] = useState<LibraryGame | null>(null);
  // Juego cuyo editor de título canónico está abierto. Igual que la portada, solo existe con `gameId`.
  const [titleGame, setTitleGame] = useState<LibraryGame | null>(null);
  const [pendingEntries, setPendingEntries] = useState<unknown[] | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const [report, setReport] = useState<LibraryImportReport | null>(null);
  // La ficha de reseña se abre con el juego agrupado completo: el juego (no la fila) es lo que se
  // reseña, y dentro el usuario elige la plataforma si hay más de una.
  const [drawerGame, setDrawerGame] = useState<LibraryGame | null>(null);
  const [manualAddOpen, setManualAddOpen] = useState(false);
  const [consoleImportOpen, setConsoleImportOpen] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  async function loadLibrary() {
    setLoading(true);
    setLoadError(null);
    try {
      setLibrary(await getLibrary());
    } catch (cause) {
      setLoadError(cause instanceof Error ? cause.message : "No se pudo cargar la biblioteca.");
    } finally {
      setLoading(false);
    }
  }

  // La reseña es por `(juego, plataforma)`, no por fila: después de guardar o borrar se actualiza en el
  // sitio cada fila que comparta la misma identidad, sin recargar la biblioteca entera ni perder el
  // scroll. La agrupación se recalcula sola porque `games` es un memo sobre `items`. Un juego puede tener
  // varias reseñas en la misma plataforma: la fila guarda la más reciente, no todas.
  function applyReview(review: Review | null, gameId: number, platform: string) {
    setLibrary((current) =>
      current === null
        ? current
        : {
            items: current.items.map((item) =>
              item.gameId === gameId && normalizeStore(item.store) === platform ? { ...item, review } : item
            )
          }
    );
  }

  // El favorito vive en el juego canónico: se actualiza en todas las filas que compartan ese id, sin
  // recargar la biblioteca ni perder el scroll. Optimista: si el servidor falla, se revierte.
  function applyFavorite(gameId: number, favorite: boolean) {
    setLibrary((current) =>
      current === null
        ? current
        : { items: current.items.map((item) => (item.gameId === gameId ? { ...item, isFavorite: favorite } : item)) }
    );
  }

  // La portada está en el juego canónico, así que el valor nuevo se pinta en todas las filas del grupo.
  function applyCover(gameId: number, imageUrl: string) {
    setLibrary((current) =>
      current === null
        ? current
        : { items: current.items.map((item) => (item.gameId === gameId ? { ...item, imageUrl } : item)) }
    );
  }

  // Una pasada escribe en el servidor, así que al terminar se recarga la biblioteca: es lo que trae las
  // portadas nuevas a las filas y lo que deja el reporte fiel a lo guardado.
  async function onSyncCovers() {
    if (coverSyncing) return;

    setCoverError(null);
    setCoverSyncing(true);
    try {
      const report = await syncLibraryCovers();
      setCoverReport(report);
      if (report.updated > 0) await loadLibrary();
    } catch (cause) {
      setCoverError(cause instanceof Error ? cause.message : "No se pudieron sincronizar las portadas");
    } finally {
      setCoverSyncing(false);
    }
  }

  // `useCallback` porque las columnas del grid dependen de este handler: sin él, cada render del padre
  // reconstruye todas las columnas.
  const onToggleFavorite = useCallback(
    async (game: LibraryGame) => {
      const gameId = game.gameId;
      if (gameId === null || favoritePendingId !== null) return;

      const next = !game.isFavorite;
      setFavoriteError(null);
      setFavoritePendingId(gameId);
      applyFavorite(gameId, next);
      try {
        await setFavorite({ gameId }, next);
      } catch (cause) {
        applyFavorite(gameId, !next);
        setFavoriteError(cause instanceof Error ? cause.message : "No se pudo actualizar el favorito.");
      } finally {
        setFavoritePendingId(null);
      }
    },
    [favoritePendingId]
  );

  useEffect(() => {
    void loadLibrary();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const items = useMemo(() => library?.items ?? [], [library]);

  // La grilla pinta juegos agrupados, no filas de tienda: un mismo título en Steam y en GOG es una sola
  // fila con sus dos plataformas. `groupLibraryItems` es pura y ya ordena por título es-MX.
  const games = useMemo(() => groupLibraryItems(items), [items]);

  // Conteo por tienda sobre juegos agrupados: el número que se muestra en el filtro es el de juegos que
  // están en esa tienda, así que un juego en dos tiendas suma en las dos y el total cuadra con «Todas».
  const storeCounts = useMemo<LibraryStoreCount[]>(() => {
    const counts = new Map<string, number>();
    for (const game of games) {
      for (const store of game.stores) {
        counts.set(store, (counts.get(store) ?? 0) + 1);
      }
    }
    return [...counts.entries()]
      .map(([store, count]) => ({ store, count }))
      .sort((left, right) => right.count - left.count || left.store.localeCompare(right.store, "es-MX"));
  }, [games]);

  // Si una tienda desaparece al recargar, el filtro vuelve a «Todas»: un select con un valor que ya no
  // existe se pinta vacío y la lista quedaría filtrada sin explicación.
  useEffect(() => {
    if (storeFilter && !storeCounts.some((entry) => entry.store === storeFilter)) {
      setStoreFilter("");
    }
  }, [storeCounts, storeFilter]);

  // Los conteos de estado y de año se calculan sobre lo ya filtrado por tienda: elegir una tienda
  // reescribe los números que quedan, así que "cuántos por año" siempre cuadra con lo que se ve.
  const storeFilteredGames = useMemo(
    () => (storeFilter ? games.filter((game) => game.stores.includes(storeFilter)) : games),
    [games, storeFilter]
  );

  const playStatusCounts = useMemo(() => {
    const counts = new Map<PlayStatusFilter, number>();
    for (const game of storeFilteredGames) {
      counts.set(game.playStatus, (counts.get(game.playStatus) ?? 0) + 1);
    }
    return counts;
  }, [storeFilteredGames]);

  // Años presentes en el conjunto filtrado, del más nuevo al más viejo. Un juego rejugado cuenta en cada
  // año en que lo jugó, así que la suma de los conteos puede superar el total de juegos.
  const playedYearCounts = useMemo(() => {
    const counts = new Map<string, number>();
    let noYear = 0;
    for (const game of storeFilteredGames) {
      if (game.playedYears.length === 0) {
        noYear += 1;
        continue;
      }
      for (const year of game.playedYears) {
        const key = String(year);
        counts.set(key, (counts.get(key) ?? 0) + 1);
      }
    }
    if (noYear > 0) counts.set(NO_YEAR, noYear);
    return counts;
  }, [storeFilteredGames]);

  const playedYearOptions = useMemo(
    () =>
      [...playedYearCounts.keys()]
        .filter((value) => value !== NO_YEAR)
        .sort((left, right) => Number(right) - Number(left))
        .map((value) => ({ value, label: value })),
    [playedYearCounts]
  );

  // Si el año elegido desaparece (lo cambia una reseña o el filtro de tienda), el filtro vuelve a «Todos»:
  // un select con un valor que ya no existe dejaría la grilla vacía sin explicación.
  useEffect(() => {
    if (yearFilter !== "all" && !playedYearCounts.has(yearFilter)) {
      setYearFilter("all");
    }
  }, [playedYearCounts, yearFilter]);

  // La paginación, el orden y el buscador se resuelven en el navegador; aquí solo se aplican los
  // tres filtros propios, y sobre ese conjunto corren búsqueda, orden y paginación.
  const filteredGames = useMemo(
    () =>
      storeFilteredGames.filter((game) => {
        if (playStatusFilter !== "all" && game.playStatus !== playStatusFilter) return false;
        if (yearFilter === "all") return true;
        return yearFilter === NO_YEAR
          ? game.playedYears.length === 0
          : game.playedYears.includes(Number(yearFilter));
      }),
    [storeFilteredGames, playStatusFilter, yearFilter]
  );

  const searchedGames = useMemo(
    () => filteredGames.filter((game) => matchesLibrarySearch(game, search)),
    [filteredGames, search]
  );

  // Sin orden explícito manda el orden del agrupador (título es-MX ascendente).
  const sortedGames = useMemo(() => {
    const sort = sorting.length > 0 ? sorting[0] : undefined;
    if (sort === undefined || !LIBRARY_SORT_OPTIONS.some((option) => option.value === sort.id)) {
      return searchedGames;
    }
    return [...searchedGames].sort((left, right) =>
      compareLibraryGames(sort.id as LibrarySortId, sort.desc, left, right)
    );
  }, [searchedGames, sorting]);

  const pageCount = Math.max(1, Math.ceil(sortedGames.length / pagination.pageSize));
  const pagedGames = useMemo(
    () => sortedGames.slice(pagination.pageIndex * pagination.pageSize, (pagination.pageIndex + 1) * pagination.pageSize),
    [sortedGames, pagination]
  );

  // El tamaño de página y las columnas visibles persisten en localStorage con las mismas claves y la
  // misma forma que usaba el grid, así que lo guardado sigue valiendo.
  useEffect(() => {
    try {
      const persistedSize = window.localStorage.getItem(LIBRARY_PAGE_SIZE_STORAGE_KEY);
      const parsedSize = Number(persistedSize);
      if (persistedSize && LIBRARY_PAGE_SIZES.includes(parsedSize)) {
        setPagination((current) =>
          current.pageIndex === 0 && current.pageSize === parsedSize
            ? current
            : { pageIndex: 0, pageSize: parsedSize }
        );
      }
      const persistedVisibility = window.localStorage.getItem(LIBRARY_COLUMN_VISIBILITY_STORAGE_KEY);
      if (persistedVisibility) {
        const parsed = JSON.parse(persistedVisibility) as unknown;
        if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
          setColumnVisibility(parsed as VisibilityState);
        }
      }
    } catch {
      // Valores corruptos o almacenamiento bloqueado: se queda lo visible por defecto.
    }
  }, []);

  // Si la lista se encoge bajo la página actual (una recarga con menos juegos), se vuelve a la última
  // página válida en vez de pintar un vacío sin explicación.
  useEffect(() => {
    setPagination((current) => {
      const maxPageIndex = Math.max(0, Math.ceil(sortedGames.length / current.pageSize) - 1);
      return current.pageIndex > maxPageIndex ? { ...current, pageIndex: maxPageIndex } : current;
    });
  }, [sortedGames.length]);

  const gamePassCount = useMemo(() => games.filter((game) => game.states.includes("subscription")).length, [games]);
  const gameCountLabel = games.length === 1 ? "1 juego" : `${games.length} juegos`;

  const onReview = useCallback((game: LibraryGame) => setDrawerGame(game), []);
  const onPickCover = useCallback((game: LibraryGame) => setCoverGame(game), []);
  const onEditTitle = useCallback((game: LibraryGame) => setTitleGame(game), []);

  // Buscar y ordenar vuelven a la primera página, igual que el reseteo automático del grid; los
  // filtros de tienda, año y estado no la mueven (son un cambio de datos externo al paginador).
  function onSearchChange(value: string) {
    setSearch(value);
    setPagination((current) => (current.pageIndex === 0 ? current : { ...current, pageIndex: 0 }));
  }

  function onSortingChange(next: SortingState) {
    setSorting(next);
    setPagination((current) => (current.pageIndex === 0 ? current : { ...current, pageIndex: 0 }));
  }

  function onPaginationChange(next: PaginationState) {
    setPagination(next);
    try {
      window.localStorage.setItem(LIBRARY_PAGE_SIZE_STORAGE_KEY, String(next.pageSize));
    } catch {
      // localStorage lleno o bloqueado: la lista sigue funcionando en memoria.
    }
  }

  function onVisibilityChange(next: VisibilityState) {
    setColumnVisibility(next);
    try {
      window.localStorage.setItem(LIBRARY_COLUMN_VISIBILITY_STORAGE_KEY, JSON.stringify(next));
    } catch {
      // localStorage lleno o bloqueado: la lista sigue funcionando en memoria.
    }
  }

  function onStoreFilterChange(event: ChangeEvent<HTMLSelectElement>) {
    setStoreFilter(event.target.value);
  }

  function onYearFilterChange(event: ChangeEvent<HTMLSelectElement>) {
    setYearFilter(event.target.value);
  }

  async function onFileChange(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const file = input.files?.[0] ?? null;
    setFileError(null);
    setImportError(null);
    setReport(null);
    setPendingEntries(null);
    if (file === null) return;

    try {
      setPendingEntries(await readImportFile(file));
    } catch (cause) {
      setFileError(cause instanceof Error ? cause.message : "No se pudo leer el archivo.");
      input.value = "";
    }
  }

  // Todo lo que se sube pasa otra vez por el BFF (límite real y validación de arreglo): la revisión del
  // navegador solo evita un viaje inútil. Al terminar, la lista se recarga en el sitio, sin navegar.
  async function runImport() {
    if (pendingEntries === null || importing) return;
    setImporting(true);
    setImportError(null);
    setReport(null);

    try {
      setReport(await importLibrary(pendingEntries));
      setPendingEntries(null);
      if (fileInputRef.current) fileInputRef.current.value = "";

      try {
        setLibrary(await getLibrary());
      } catch {
        setImportError("La importación terminó, pero no se pudo recargar la lista. Recarga la página para verla.");
      }
    } catch (cause) {
      setImportError(cause instanceof Error ? cause.message : "No se pudo importar la biblioteca.");
    } finally {
      setImporting(false);
    }
  }

  return (
    <div className="space-y-4">
      <section className="app-card-accent space-y-4 p-5" aria-labelledby="library-import-heading">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Importación</p>
          <h2 id="library-import-heading" className="text-xl font-semibold tracking-tight text-primary">
            Tu biblioteca de Playnite
          </h2>
          <p className="text-xs text-muted">
            Sube el archivo JSON que exporta Playnite: cada juego se guarda con su tienda, su fecha de alta y si
            está instalado. La importación es manual, actualiza lo que ya existe y no borra nada; el archivo no
            se guarda en el servidor.
          </p>
        </div>

        <div className="space-y-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4">
          <div className="flex items-center gap-2">
            <FileJson className="h-4 w-4 shrink-0 text-muted" aria-hidden="true" />
            <label htmlFor="library-import-file" className="text-sm font-semibold text-primary">
              Archivo JSON del export
            </label>
          </div>
          <input
            id="library-import-file"
            ref={fileInputRef}
            type="file"
            accept=".json,application/json"
            onChange={(event) => void onFileChange(event)}
            disabled={importing}
            aria-describedby="library-import-hint"
            className="input-semantic block min-h-10 w-full cursor-pointer px-3 py-1.5 text-sm file:mr-3 file:cursor-pointer file:rounded-[var(--radius-sm)] file:border-0 file:bg-[var(--color-surface-3)] file:px-3 file:py-1 file:text-sm file:font-semibold file:text-primary"
          />
          <p id="library-import-hint" className="text-xs text-muted">
            Solo <code>.json</code>, máximo 10 MiB y con un arreglo en la raíz, tal como lo produce Playnite. Se
            revisa en tu navegador antes de enviarlo.
          </p>
          <div className="flex flex-wrap items-center gap-3">
            <Button
              type="button"
              variant="secondary"
              loading={importing}
              loadingText="Importando..."
              disabled={pendingEntries === null}
              onClick={() => void runImport()}
            >
              <Upload className="h-4 w-4" aria-hidden="true" />
              Importar
            </Button>
            <span className="text-xs text-muted" aria-live="polite">
              {importing ? "Enviando el archivo..." : pendingEntries ? "Archivo listo para importar." : ""}
            </span>
          </div>
          {fileError ? <Alert variant="danger">{fileError}</Alert> : null}
        </div>

        {importError ? <Alert variant="danger">{importError}</Alert> : null}
        {report ? <ImportReport report={report} /> : null}
      </section>

      <section className="app-card space-y-4 p-5" aria-labelledby="library-items-heading">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Biblioteca</p>
          <h2 id="library-items-heading" className="text-xl font-semibold tracking-tight text-primary">
            Tus juegos
          </h2>
          <div className="flex flex-wrap items-center gap-2">
            <span className="tabler-badge tabler-badge-primary">{gameCountLabel}</span>
            <span className="tabler-badge tabler-badge-info">
              {storeCounts.length === 1 ? "1 tienda" : `${storeCounts.length} tiendas`}
            </span>
            {gamePassCount > 0 ? (
              <span className="tabler-badge tabler-badge-solid tabler-badge-xbox">{gamePassCount} en Game Pass</span>
            ) : null}
            <Button
              type="button"
              variant="secondary"
              className="h-8 whitespace-nowrap px-3 text-xs"
              onClick={() => setManualAddOpen(true)}
            >
              <Gamepad2 className="h-4 w-4" aria-hidden="true" />
              Añadir manual
            </Button>
            <Button
              type="button"
              variant="secondary"
              className="h-8 whitespace-nowrap px-3 text-xs"
              onClick={() => setConsoleImportOpen(true)}
            >
              <Upload className="h-4 w-4" aria-hidden="true" />
              Importar consolas
            </Button>
          </div>
          <p className="text-xs text-muted">
            Cada fila es un juego, no una entrada de tienda: si el mismo título está en varias plataformas se
            muestra una sola vez, con todas sus tiendas y sus estados. Cuando la fila mezcla estados (comprado
            y además en Game Pass, por ejemplo) se pintan los dos, con la suscripción primero, para que no se
            lea como compra donde solo hay suscripción. La lista se pagina, se ordena y se busca en tu
            navegador; el menú «Columnas» permite ocultar las secciones que no uses.
          </p>
          <p className="text-xs text-muted">
            Las reseñas son por juego y plataforma, y puedes tener varias: cada vez que lo terminas agregas
            una nueva sin perder las anteriores. «Reseñar» (o «Reseñas») abre la ficha lateral, donde está la
            lista completa de la plataforma elegida —y el selector, si el juego está en varias tiendas
            reseñables— para editar una vieja o crear otra. La nota va de 0 a 100 y su etiqueta
            (malo…obra maestra) la calcula el servidor al guardar; aquí no se replica. Un juego que el catálogo
            todavía no reconoce no se puede reseñar y la fila lo dice.
          </p>
          <p className="text-xs text-muted">
            «Editar título» corrige el nombre del juego en el catálogo, y aparece solo en las filas que el
            catálogo ya reconoce. Ese nombre es <strong>compartido</strong>: al guardarlo cambia a la vez en
            todas las copias vinculadas al mismo juego, y nunca el texto que Playnite importó. Puedes
            escribirlo a mano o buscar el título oficial en IGDB y quedarte con el resultado correcto; al
            elegir un resultado, su id queda vinculado al juego.
          </p>
          <p className="text-xs text-muted">
            Cuando el mismo título aparece como dos juegos canónicos distintos, la biblioteca muestra una fila
            por cada uno. Eso se corrige a mano en{" "}
            <Link
              href="/library/duplicates"
              className="inline-flex items-center gap-1 font-semibold text-accent underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              <GitMerge className="h-3.5 w-3.5" aria-hidden="true" />
              Duplicados del catálogo
            </Link>
            , donde se elige el juego que sobrevive. La fusión es irreversible.
          </p>
          <p className="text-xs text-muted">
            Para dar de alta una colección entera de consola, «Importar consolas» acepta el mismo export de Playnite
            (también con filas de tienda, que se descartan): primero muestra qué entró y qué no, después pide la
            plataforma de cada juego y si es nuevo o comparte ficha con uno que ya exista, y solo entonces escribe.
          </p>
        </div>

        {favoriteError ? <Alert variant="danger">{favoriteError}</Alert> : null}
        {coverError ? <Alert variant="danger">{coverError}</Alert> : null}
        {coverReport ? <CoverSyncReportBadges report={coverReport} /> : null}

        {manualAddOpen ? (
          <ManualAddDialog onClose={() => setManualAddOpen(false)} onAdded={() => void loadLibrary()} />
        ) : null}

        {consoleImportOpen ? (
          <ConsoleImportDialog
            onClose={() => setConsoleImportOpen(false)}
            onImported={() => void loadLibrary()}
          />
        ) : null}

        {loading ? (
          <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
            Cargando...
          </p>
        ) : loadError ? (
          <div className="space-y-3">
            <Alert variant="danger">{loadError}</Alert>
            <Button type="button" variant="secondary" onClick={() => void loadLibrary()}>
              Reintentar
            </Button>
          </div>
        ) : items.length === 0 ? (
          <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
            Tu biblioteca está vacía. Sube el JSON del export de Playnite para llenarla.
          </p>
        ) : (
          <div className="space-y-4">
            <div className="flex flex-wrap items-end gap-3">
              <div className="w-full sm:w-64">
                <Select
                  id="library-store-filter"
                  label="Filtrar por tienda"
                  value={storeFilter}
                  onChange={onStoreFilterChange}
                >
                  <option value="">Todas las tiendas ({games.length})</option>
                  {storeCounts.map((entry) => (
                    <option key={entry.store} value={entry.store}>
                      {storeLabel(entry.store)} ({entry.count})
                    </option>
                  ))}
                </Select>
              </div>
              <div className="w-full sm:w-52">
                <Select id="library-year-filter" label="Filtrar por año jugado" value={yearFilter} onChange={onYearFilterChange}>
                  <option value="all">Todos los años</option>
                  {playedYearOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label} ({playedYearCounts.get(option.value) ?? 0})
                    </option>
                  ))}
                  {playedYearCounts.has(NO_YEAR) ? (
                    <option value={NO_YEAR}>Sin año ({playedYearCounts.get(NO_YEAR) ?? 0})</option>
                  ) : null}
                </Select>
              </div>
              <FilterToggle
                id="library-play-status-filter"
                label="Filtrar por estado de juego"
                options={PLAY_STATUS_FILTERS}
                value={playStatusFilter}
                counts={playStatusCounts}
                totalCount={storeFilteredGames.length}
                onChange={setPlayStatusFilter}
              />
              <div className="space-y-1.5">
                <p className="text-sm font-medium text-primary">Portadas</p>
                <Button
                  type="button"
                  variant="secondary"
                  className="h-9 whitespace-nowrap px-3 text-xs"
                  loading={coverSyncing}
                  onClick={() => void onSyncCovers()}
                >
                  Sincronizar portadas
                </Button>
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
              <LibrarySearchInput value={search} onChange={onSearchChange} />
              <LibrarySortControl sorting={sorting} onSortingChange={onSortingChange} />
              <LibraryColumnsMenu visibility={columnVisibility} onChange={onVisibilityChange} />
            </div>
            {sortedGames.length === 0 ? (
              <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
                Ningún juego coincide con los filtros o la búsqueda.
              </p>
            ) : (
              <>
                <ul className="grid gap-3 md:grid-cols-2" aria-label="Juegos en la biblioteca">
                  {pagedGames.map((game) => (
                    <LibraryCard
                      key={game.key}
                      game={game}
                      visibility={columnVisibility}
                      favoritePending={favoritePendingId === game.gameId}
                      onToggleFavorite={(target) => void onToggleFavorite(target)}
                      onReview={onReview}
                      onPickCover={onPickCover}
                      onEditTitle={onEditTitle}
                    />
                  ))}
                </ul>
                <LibraryPager pagination={pagination} pageCount={pageCount} onPaginationChange={onPaginationChange} />
              </>
            )}
          </div>
        )}
      </section>

      {coverGame !== null && coverGame.gameId !== null ? (
        <CoverPicker
          gameId={coverGame.gameId}
          title={coverGame.title}
          stores={coverGame.stores}
          linkedRows={coverGame.platforms.length}
          onClose={() => setCoverGame(null)}
          onPicked={(gameId, imageUrl) => {
            applyCover(gameId, imageUrl);
            setCoverGame(null);
          }}
        />
      ) : null}

      {titleGame !== null && titleGame.gameId !== null ? (
        <TitleEditor
          key={titleGame.key}
          gameId={titleGame.gameId}
          title={titleGame.title}
          linkedRows={titleGame.platforms.length}
          stores={titleGame.stores}
          onClose={() => setTitleGame(null)}
          onSaved={() => void loadLibrary()}
        />
      ) : null}

      {drawerGame !== null ? (
        <ReviewDrawer
          key={drawerGame.key}
          game={drawerGame}
          onClose={() => setDrawerGame(null)}
          onReviewsChanged={(gameId, platform, review) => {
            // La grilla guarda una sola reseña por fila: el drawer manda la representativa (la más
            // reciente que queda) y aquí se pinta, sin recargar la biblioteca.
            applyReview(review, gameId, platform);
            setDrawerGame(null);
          }}
        />
      ) : null}
    </div>
  );
}
