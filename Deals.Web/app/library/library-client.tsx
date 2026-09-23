"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";
import Link from "next/link";
import type { ColumnDef, FilterFn, SortingFn } from "@tanstack/react-table";
import { FileJson, Gamepad2, GitMerge, HardDriveDownload, Star, Trophy, Upload } from "lucide-react";
import { DataGrid } from "@/components/data-grid/data-grid";
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

// Buscador global: por título y por tienda. Es el filtro que el DataGrid aplica a la fila completa; se
// ignoran `columnId` y el valor de cada columna porque lo que importa es el juego agrupado, no una celda.
const libraryFilter: FilterFn<LibraryGame> = (row, _columnId, value) => {
  const query = fold(String(value).trim());
  if (query === "") return true;

  const game = row.original;
  if (fold(game.title).includes(query)) return true;
  return game.stores.some((store) => fold(storeLabel(store)).includes(query));
};

// Un valor ausente se expresa como `undefined`, nunca `null`, y cada columna ordenable lleva
// `sortUndefined: "last"`. El paquete resuelve ese caso con un `return` temprano ANTES de invertir por
// dirección, así que los juegos sin nota o sin año quedan al final tanto en asc como en desc. El default
// (`sortUndefined: 1`) sí se invierte: con él, ordenar descendente pondría arriba los vacíos como si
// fueran los valores más altos.
const numericSort: SortingFn<LibraryGame> = (rowA, rowB, columnId) =>
  Number(rowA.getValue(columnId)) - Number(rowB.getValue(columnId));

const titleSort: SortingFn<LibraryGame> = (rowA, rowB, columnId) =>
  String(rowA.getValue(columnId)).localeCompare(String(rowB.getValue(columnId)), "es-MX");

// Filtro de estado de juego del toolbar. El DataGrid solo sabe filtrar columnas con un input de texto, así
// que un estado derivado no se puede resolver bien en la fila de filtros: se resuelve aquí, con el conteo
// de cada opción para que el filtro sea también el reporte ("cuántos por año / por estado").
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
// total: "Sin appid" son los juegos que la pasada no puede resolver sola y que necesitan el selector.
function CoverSyncReportBadges({ report }: { readonly report: LibraryCoverSyncReport }) {
  return (
    <div className="space-y-2" aria-live="polite">
      <p className="text-sm font-semibold text-primary">Resultado de la sincronización de portadas</p>
      <div className="flex flex-wrap items-center gap-2">
        <span className="tabler-badge tabler-badge-success">Puestas {report.updated}</span>
        <span className={cn("tabler-badge", report.failed > 0 ? "tabler-badge-warning" : "tabler-badge-muted")}>
          Fallidas {report.failed}
        </span>
        <span className="tabler-badge tabler-badge-info">Pendientes de otra pasada {report.remaining}</span>
        <span className="tabler-badge tabler-badge-muted">Sin appid de Steam {report.missingWithoutSteamId}</span>
        <span className="tabler-badge tabler-badge-muted">Sin portada {report.missing}</span>
      </div>
      <p className="text-xs text-muted">
        Cada pasada revisa hasta 25 juegos y solo rellena portadas que falten: nunca reemplaza una que ya
        exista. «Sin appid de Steam» son los juegos que el catálogo no liga a Steam; para esos usa
        «Portada» en la fila y elige el resultado a mano.
      </p>
    </div>
  );
}

// Orden del ciclo de vida para la columna de estado: el índice es el valor que ordena.
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

export function LibraryClient() {
  const [library, setLibrary] = useState<LibraryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [storeFilter, setStoreFilter] = useState("");
  const [playStatusFilter, setPlayStatusFilter] = useState<PlayStatusFilter>("all");
  const [yearFilter, setYearFilter] = useState<string>("all");
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

  // La paginación, el orden y el buscador global los resuelve el DataGrid en el navegador; aquí solo se
  // aplican los tres filtros propios, así que el grid recibe ya la lista que debe paginar.
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

  const gamePassCount = useMemo(() => games.filter((game) => game.states.includes("subscription")).length, [games]);
  const gameCountLabel = games.length === 1 ? "1 juego" : `${games.length} juegos`;

  const onReview = useCallback((game: LibraryGame) => setDrawerGame(game), []);
  const onPickCover = useCallback((game: LibraryGame) => setCoverGame(game), []);
  const onEditTitle = useCallback((game: LibraryGame) => setTitleGame(game), []);

  const columns = useMemo<ColumnDef<LibraryGame>[]>(
    () => [
      {
        id: "cover",
        header: "Portada",
        enableSorting: false,
        cell: ({ row }) => <LibraryThumb src={row.original.imageUrl} />
      },
      {
        id: "title",
        accessorKey: "title",
        header: "Juego",
        sortingFn: titleSort,
        cell: ({ row }) => <p className="min-w-48 text-sm font-semibold text-primary">{row.original.title}</p>
      },
      {
        id: "stores",
        header: "Tiendas",
        enableSorting: false,
        cell: ({ row }) => (
          <div className="flex flex-wrap items-center gap-2">
            {row.original.stores.map((store) => (
              <StoreBadge key={store} store={store} />
            ))}
          </div>
        )
      },
      {
        id: "state",
        header: "Estado",
        enableSorting: false,
        cell: ({ row }) => <StateCell game={row.original} />
      },
      {
        id: "playStatus",
        // El orden del header es el del ciclo de vida: por jugar, terminado, completado, dropeado.
        accessorFn: (game) => PLAY_STATUS_ORDER[game.playStatus],
        header: "Estado de juego",
        sortingFn: numericSort,
        cell: ({ row }) => <PlayStatusBadge game={row.original} />
      },
      {
        id: "favorite",
        accessorFn: (game) => (game.isFavorite ? 1 : 0),
        header: "Favorito",
        sortingFn: numericSort,
        enableSorting: false,
        cell: ({ row }) => (
          <FavoriteToggle
            game={row.original}
            pending={favoritePendingId === row.original.gameId}
            onToggle={(game) => void onToggleFavorite(game)}
          />
        )
      },
      {
        id: "score",
        accessorFn: (game) => game.lastReview?.score ?? undefined,
        header: "Última reseña",
        sortingFn: numericSort,
        sortUndefined: "last",
        cell: ({ row }) =>
          row.original.lastReview === null ? (
            <span className="text-muted">—</span>
          ) : (
            <ReviewBadges review={row.original.lastReview} />
          )
      },
      {
        id: "playedYears",
        // Ordena por el año más reciente: un juego rejugado vale por su última partida.
        accessorFn: (game) => game.playedYears[0] ?? undefined,
        header: "Años jugados",
        sortingFn: numericSort,
        sortUndefined: "last",
        cell: ({ row }) =>
          row.original.playedYears.length === 0 ? (
            <span className="text-muted">—</span>
          ) : (
            <div className="flex flex-wrap items-center gap-1">
              {row.original.playedYears.map((year) => (
                <span key={year} className="tabler-badge tabler-badge-muted tabular-nums">
                  {year}
                </span>
              ))}
            </div>
          )
      },
      {
        id: "actions",
        header: "Acciones",
        enableSorting: false,
        cell: ({ row }) => (
          <div className="flex flex-wrap items-center gap-2">
            <ReviewAction game={row.original} onReview={onReview} />
            <CoverAction game={row.original} onPickCover={onPickCover} />
            <TitleAction game={row.original} onEditTitle={onEditTitle} />
          </div>
        )
      }
    ],
    [favoritePendingId, onEditTitle, onPickCover, onReview, onToggleFavorite]
  );

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
            navegador; el menú «Columnas» permite ocultar las que no uses.
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
          <DataGrid
            columns={columns}
            rows={filteredGames}
            density="compact"
            stickyHeader
            stickyActionsColumn
            pageSizeOptions={[10, 25, 50, 100]}
            pageSizeStorageKey="library.pageSize.v1"
            enableColumnVisibility
            columnVisibilityStorageKey="library.columns.v1"
            enableGlobalFilter
            globalFilterPlaceholder="Buscar por juego o tienda"
            globalFilterFn={libraryFilter}
            toolbar={
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
                    Sincronizar con Steam
                  </Button>
                </div>
              </div>
            }
            emptyMessage="Ningún juego coincide con los filtros o la búsqueda."
          />
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
