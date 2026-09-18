"use client";

import { useEffect, useMemo, useRef, useState, type ChangeEvent, type FormEvent } from "react";
import { FileJson, HardDriveDownload, Trophy, Upload } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { PriceValue } from "@/components/ui/price-value";
import { cn } from "@/lib/ui/cn";
import { storeLabel, toStoreKey, type StoreKey } from "@/lib/contracts/stores";
import { formatReviewMonth, type Review } from "@/lib/contracts/reviews";
import { getLibrary, importLibrary } from "./_lib/library-api";
import { createReview, deleteReview, updateReview } from "./_lib/reviews-api";
import {
  LIBRARY_IMPORT_MAX_BYTES,
  type LibraryImportReport,
  type LibraryItem,
  type LibraryResponse,
  type LibraryState,
  type LibraryStoreCount
} from "./_lib/library-contract";

// El backend guarda los timestamps en UTC y los manda normalizados a `...Z`; se formatean en UTC para
// que un `Added` de Playnite no cambie de día por la zona del navegador.
const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeZone: "UTC" });

function formatDate(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormatter.format(date);
}

// El nombre visible de cada tienda vive en `lib/contracts/stores.ts` y lo comparte con los badges de
// posesión del detalle: una tienda fuera del catálogo se muestra con su propio texto, nunca se oculta.

// Filas pintadas de golpe. La biblioteca real trae ~2.6k entradas: con paginación local la lista no se
// vuelve pesada y el usuario nunca pierde la posición al cargar más.
const PAGE_SIZE = 100;

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

// Game Pass no es posesión: el tag es sólido y el más visible de la fila, y la fila nunca lleva lenguaje
// de compra ni de precio.
function StateBadge({ state }: { readonly state: LibraryState }) {
  if (state === "subscription") {
    return <span className="tabler-badge tabler-badge-solid tabler-badge-primary">Game Pass</span>;
  }
  return state === "wished" ? (
    <span className="tabler-badge tabler-badge-info">Wishlist</span>
  ) : (
    <span className="tabler-badge tabler-badge-muted">En tu biblioteca</span>
  );
}

function StoreBadge({ store }: { readonly store: string }) {
  return <span className="text-[10px] font-semibold uppercase tracking-widest text-muted">{storeLabel(store)}</span>;
}

// Los dos mínimos ya vienen convertidos por el backend: su moneda es siempre MXN y no se presenta como
// aproximación porque no lo es.
const MXN = "MXN";

// Precios de una fila ligada. Un importe sin su moneda no se pinta a medias: el campo se omite, y si no
// queda ninguno la fila degrada a un único "Sin precio" en vez de desaparecer.
function LibraryPrices({ item }: { readonly item: LibraryItem }) {
  const fields = [
    { label: "Oficial", amountMinor: item.bestOfficialMinor, currency: MXN },
    { label: "Keys", amountMinor: item.bestKeyshopMinor, currency: MXN },
    { label: "Base", amountMinor: item.basePriceMinor, currency: item.baseCurrency },
    // El mínimo histórico se agrega solo sobre ofertas guardadas en MXN (HistoryLowCurrency == "MXN"),
    // así que su importe siempre es pesos: no se pinta con la moneda del proveedor.
    { label: "Mín. histórico", amountMinor: item.historyLowMinor, currency: MXN }
  ].filter((field) => field.amountMinor !== null && field.currency !== null);

  if (fields.length === 0) return <span className="text-xs text-muted">Sin precio</span>;

  return (
    <>
      {fields.map((field) => (
        <span key={field.label} className="inline-flex items-baseline gap-1 text-xs">
          <span className="text-muted">{field.label}</span>
          <PriceValue amountMinor={field.amountMinor} currency={field.currency} />
        </span>
      ))}
    </>
  );
}

// Estado de precio de la fila. Game Pass no lleva precio ni lenguaje de propiedad; `none` conserva la
// nota explícita; un candidato por título añade la etiqueta que impide leerlo como identidad confirmada.
function LibraryPriceState({ item }: { readonly item: LibraryItem }) {
  if (item.state === "subscription" || item.priceState === "subscription") return null;
  if (item.priceState === "none") return <p className="text-xs text-muted">Sin precios vinculados</p>;

  return (
    <>
      {item.priceState === "title_candidate" ? (
        <span className="tabler-badge tabler-badge-warning">Precio vinculado por título</span>
      ) : null}
      <LibraryPrices item={item} />
    </>
  );
}

// Reseña guardada, en solo lectura. La etiqueta de la nota llega del servidor: aquí no se calcula ni se
// replica ningún rango.
function ReviewSummary({ review }: { readonly review: Review }) {
  const started = formatReviewMonth(review.startedMonth);
  const finished = formatReviewMonth(review.finishedMonth);
  const range =
    started && finished ? `De ${started} a ${finished}` : started ? `Desde ${started}` : finished ? `Hasta ${finished}` : null;

  return (
    <div className="space-y-1">
      <div className="flex flex-wrap items-center gap-2">
        {review.score !== null ? (
          <span className="tabler-badge tabler-badge-info">
            Nota {review.score}
            {review.scoreLabel ? ` · ${review.scoreLabel}` : ""}
          </span>
        ) : (
          <span className="tabler-badge tabler-badge-muted">Sin nota</span>
        )}
        {review.isGoty ? (
          <span className="tabler-badge tabler-badge-success">
            <Trophy className="h-3 w-3" aria-hidden="true" />
            GOTY
          </span>
        ) : null}
        {range ? <span className="text-xs text-muted">{range}</span> : null}
      </div>
      {review.body ? <p className="whitespace-pre-wrap break-words text-sm text-secondary">{review.body}</p> : null}
    </div>
  );
}

// La nota escrita se valida acá (forma), no se etiqueta: el rango lo decide el servidor. `null` es "sin
// nota" y `"invalid"` una entrada que no es entero 0-100.
function readScoreInput(raw: string): number | null | "invalid" {
  const text = raw.trim();
  if (text === "") return null;
  const value = Number(text);
  return Number.isInteger(value) && value >= 0 && value <= 100 ? value : "invalid";
}

interface ReviewEditorProps {
  readonly domId: number;
  readonly gameId: number;
  readonly platform: StoreKey;
  readonly existing: Review | null;
  readonly onSaved: (review: Review) => void;
  readonly onCancel: () => void;
}

function ReviewEditor({ domId, gameId, platform, existing, onSaved, onCancel }: ReviewEditorProps) {
  const [startedMonth, setStartedMonth] = useState(existing?.startedMonth ?? "");
  const [finishedMonth, setFinishedMonth] = useState(existing?.finishedMonth ?? "");
  const [score, setScore] = useState(existing?.score !== null && existing?.score !== undefined ? String(existing.score) : "");
  const [isGoty, setIsGoty] = useState(existing?.isGoty ?? false);
  const [body, setBody] = useState(existing?.body ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saving) return;

    const parsedScore = readScoreInput(score);
    if (parsedScore === "invalid") {
      setError("La nota debe ser un número entero entre 0 y 100.");
      return;
    }

    const fields = {
      startedMonth: startedMonth || null,
      finishedMonth: finishedMonth || null,
      score: parsedScore,
      isGoty,
      body: body.trim() ? body : null
    };

    setSaving(true);
    setError(null);
    try {
      // El mismo formulario crea o edita: la reseña existente manda `reviewId`; una nueva manda su
      // identidad `(gameId, platform)`.
      const saved = existing
        ? await updateReview(existing.reviewId, fields)
        : await createReview({ gameId, platform, ...fields });
      onSaved(saved);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo guardar la reseña.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <form
      className="space-y-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4"
      onSubmit={(event) => void onSubmit(event)}
    >
      <p className="text-xs font-semibold uppercase tracking-widest text-muted">
        {existing ? "Editar reseña" : "Nueva reseña"} · {storeLabel(platform)}
      </p>

      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label htmlFor={`review-start-${domId}`} className="mb-1 block text-xs font-semibold uppercase tracking-widest text-muted">
            Inicio
          </label>
          <input
            id={`review-start-${domId}`}
            type="month"
            className="input-semantic h-10 w-full px-3 text-sm"
            value={startedMonth}
            onChange={(event) => setStartedMonth(event.target.value)}
          />
        </div>
        <div>
          <label htmlFor={`review-end-${domId}`} className="mb-1 block text-xs font-semibold uppercase tracking-widest text-muted">
            Fin
          </label>
          <input
            id={`review-end-${domId}`}
            type="month"
            className="input-semantic h-10 w-full px-3 text-sm"
            value={finishedMonth}
            onChange={(event) => setFinishedMonth(event.target.value)}
          />
        </div>
      </div>

      <div className="space-y-1">
        <label htmlFor={`review-score-${domId}`} className="block text-xs font-semibold uppercase tracking-widest text-muted">
          Nota (0-100)
        </label>
        <input
          id={`review-score-${domId}`}
          type="number"
          inputMode="numeric"
          min={0}
          max={100}
          step={1}
          className="input-semantic h-10 w-24 px-3 text-sm"
          value={score}
          onChange={(event) => setScore(event.target.value)}
        />
        <p className="text-xs text-muted">La etiqueta de la nota (malo…obra maestra) la calcula el servidor al guardar.</p>
      </div>

      <div className="flex items-center gap-2">
        <input
          id={`review-goty-${domId}`}
          type="checkbox"
          className="h-4 w-4"
          checked={isGoty}
          onChange={(event) => setIsGoty(event.target.checked)}
        />
        <label htmlFor={`review-goty-${domId}`} className="text-sm font-semibold text-primary">
          GOTY
        </label>
      </div>

      <div>
        <label htmlFor={`review-body-${domId}`} className="mb-1 block text-xs font-semibold uppercase tracking-widest text-muted">
          Reseña
        </label>
        <textarea
          id={`review-body-${domId}`}
          rows={3}
          className="input-semantic w-full px-3 py-2 text-sm"
          value={body}
          onChange={(event) => setBody(event.target.value)}
        />
      </div>

      {error ? <Alert variant="danger">{error}</Alert> : null}

      <div className="flex flex-wrap items-center gap-2">
        <Button type="submit" variant="primary" loading={saving} loadingText="Guardando...">
          Guardar
        </Button>
        <Button type="button" variant="secondary" disabled={saving} onClick={onCancel}>
          Cancelar
        </Button>
      </div>
    </form>
  );
}

interface ReviewBlockProps {
  readonly item: LibraryItem;
  readonly editing: boolean;
  readonly onEdit: () => void;
  readonly onCancel: () => void;
  readonly onSaved: (review: Review) => void;
  readonly onDeleted: (review: Review) => void;
}

// Una fila de biblioteca es un par `(juego, plataforma)`, que es exactamente una reseña. La fila sin
// `gameId` no se oculta ni ofrece una acción rota: dice por qué todavía no se puede reseñar.
function ReviewBlock({ item, editing, onEdit, onCancel, onSaved, onDeleted }: ReviewBlockProps) {
  const gameId = item.gameId;
  const platform = toStoreKey(item.store);
  const review = item.review;
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  if (gameId === null) {
    return (
      <p className="text-xs text-muted">
        Todavía no tiene identidad en el catálogo, así que no se puede reseñar. La reseña aparecerá aquí cuando
        el catálogo lo reconozca.
      </p>
    );
  }

  if (platform === null) {
    return (
      <p className="text-xs text-muted">
        {storeLabel(item.store)} no está en el vocabulario de plataformas, así que esta entrada no se puede reseñar.
      </p>
    );
  }

  if (editing) {
    return (
      <ReviewEditor domId={item.userLibraryId} gameId={gameId} platform={platform} existing={review} onSaved={onSaved} onCancel={onCancel} />
    );
  }

  async function onDelete() {
    if (!review || deleting) return;
    setDeleting(true);
    setDeleteError(null);
    try {
      await deleteReview(review.reviewId);
      onDeleted(review);
    } catch (cause) {
      setDeleteError(cause instanceof Error ? cause.message : "No se pudo borrar la reseña.");
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div className="space-y-2">
      {review ? (
        <>
          <ReviewSummary review={review} />
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="secondary" onClick={onEdit}>
              Editar reseña
            </Button>
            <Button type="button" variant="danger" loading={deleting} loadingText="Borrando..." onClick={() => void onDelete()}>
              Borrar
            </Button>
          </div>
        </>
      ) : (
        <Button type="button" variant="secondary" onClick={onEdit}>
          Reseñar
        </Button>
      )}
      {deleteError ? <Alert variant="danger">{deleteError}</Alert> : null}
    </div>
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

export function LibraryClient() {
  const [library, setLibrary] = useState<LibraryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [storeFilter, setStoreFilter] = useState("");
  const [visibleCount, setVisibleCount] = useState(PAGE_SIZE);
  const [pendingEntries, setPendingEntries] = useState<unknown[] | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const [report, setReport] = useState<LibraryImportReport | null>(null);
  // Solo un editor de reseña abierto a la vez: el par `(juego, plataforma)` ya identifica la reseña y la
  // fila solo aporta el asiento del formulario.
  const [editingKey, setEditingKey] = useState<number | null>(null);
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
  // sitio cada fila que comparta la misma identidad, sin recargar la biblioteca entera ni perder el scroll.
  function applyReview(review: Review | null, gameId: number, platform: StoreKey) {
    setLibrary((current) =>
      current === null
        ? current
        : {
            items: current.items.map((item) =>
              item.gameId === gameId && toStoreKey(item.store) === platform ? { ...item, review } : item
            )
          }
    );
  }

  useEffect(() => {
    void loadLibrary();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const items = useMemo(() => library?.items ?? [], [library]);

  const storeCounts = useMemo<LibraryStoreCount[]>(() => {
    const counts = new Map<string, number>();
    for (const item of items) {
      counts.set(item.store, (counts.get(item.store) ?? 0) + 1);
    }
    return [...counts.entries()]
      .map(([store, count]) => ({ store, count }))
      .sort((left, right) => right.count - left.count || left.store.localeCompare(right.store, "es-MX"));
  }, [items]);

  // Si una tienda desaparece al recargar, el filtro vuelve a «Todas»: un select con un valor que ya no
  // existe se pinta vacío y la lista quedaría filtrada sin explicación.
  useEffect(() => {
    if (storeFilter && !storeCounts.some((entry) => entry.store === storeFilter)) {
      setStoreFilter("");
    }
  }, [storeCounts, storeFilter]);

  const gamePassCount = items.filter((item) => item.state === "subscription").length;
  const filtered = storeFilter ? items.filter((item) => item.store === storeFilter) : items;
  const visible = filtered.slice(0, visibleCount);

  function onStoreFilterChange(event: ChangeEvent<HTMLSelectElement>) {
    setStoreFilter(event.target.value);
    setVisibleCount(PAGE_SIZE);
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
            Juegos por tienda
          </h2>
          <div className="flex flex-wrap items-center gap-2">
            <span className="tabler-badge tabler-badge-muted">
              {items.length === 1 ? "1 juego" : `${items.length} juegos`}
            </span>
            <span className="tabler-badge tabler-badge-muted">
              {storeCounts.length === 1 ? "1 tienda" : `${storeCounts.length} tiendas`}
            </span>
            {gamePassCount > 0 ? (
              <span className="tabler-badge tabler-badge-solid tabler-badge-primary">{gamePassCount} en Game Pass</span>
            ) : null}
          </div>
          <p className="text-xs text-muted">
            Los precios se muestran solo cuando hay un vínculo con el comparador: «Oficial» y «Keys» ya están
            en MXN, y «Base» y «Mín. histórico» van en la moneda del proveedor, sin convertir. «Precio
            vinculado por título» avisa que la coincidencia es por nombre y puede ser otra edición; «Sin
            precios vinculados» solo significa que el título todavía no está en el catálogo. Los juegos de
            Game Pass no muestran precio porque es una suscripción y puede terminar.
          </p>
          <p className="text-xs text-muted">
            Cada fila también es una reseña: una por juego y plataforma. Usa «Reseñar» para poner una nota de
            0 a 100, los meses de inicio y fin, la marca GOTY y el texto. La etiqueta de la nota
            (malo…obra maestra) la calcula el servidor al guardar; aquí no se replica.
          </p>
        </div>

        {items.length > 0 ? (
          <div className="w-full sm:max-w-xs">
            <label htmlFor="library-store-filter" className="mb-1 block text-xs font-semibold uppercase tracking-widest text-muted">
              Filtrar por tienda
            </label>
            <select
              id="library-store-filter"
              className="input-semantic h-10 w-full px-3 text-sm"
              value={storeFilter}
              onChange={onStoreFilterChange}
            >
              <option value="">Todas las tiendas ({items.length})</option>
              {storeCounts.map((entry) => (
                <option key={entry.store} value={entry.store}>
                  {storeLabel(entry.store)} ({entry.count})
                </option>
              ))}
            </select>
          </div>
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
        ) : filtered.length === 0 ? (
          <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
            No hay juegos de {storeLabel(storeFilter)} en la biblioteca.
          </p>
        ) : (
          <>
            <p className="text-xs text-muted" aria-live="polite">
              Mostrando {visible.length} de {filtered.length} {filtered.length === 1 ? "juego" : "juegos"}
              {storeFilter ? ` de ${storeLabel(storeFilter)}` : ""}.
            </p>
            <ul className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)]">
              {visible.map((item) => {
                const added = formatDate(item.addedAt);
                return (
                  <li
                    key={item.userLibraryId}
                    className="flex flex-col gap-2 border-t border-default p-3 first:border-t-0"
                  >
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-4">
                      <div className="min-w-0 flex-1">
                        <p className="text-sm font-semibold text-primary">{item.title}</p>
                        <div className="mt-1 flex flex-wrap items-center gap-2">
                          <StoreBadge store={item.store} />
                          <StateBadge state={item.state} />
                          {item.isInstalled ? (
                            <span className="tabler-badge tabler-badge-info">
                              <HardDriveDownload className="h-3 w-3" aria-hidden="true" />
                              Instalado
                            </span>
                          ) : null}
                        </div>
                      </div>
                      <div className="flex shrink-0 flex-col gap-1 sm:items-end">
                        <p className="text-xs text-muted">{added ? `Alta ${added}` : "Sin fecha de alta"}</p>
                        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 sm:justify-end">
                          <LibraryPriceState item={item} />
                        </div>
                      </div>
                    </div>
                    <ReviewBlock
                      item={item}
                      editing={editingKey === item.userLibraryId}
                      onEdit={() => setEditingKey(item.userLibraryId)}
                      onCancel={() => setEditingKey(null)}
                      onSaved={(review) => {
                        applyReview(review, review.gameId, review.platform);
                        setEditingKey(null);
                      }}
                      onDeleted={(review) => {
                        applyReview(null, review.gameId, review.platform);
                        setEditingKey(null);
                      }}
                    />
                  </li>
                );
              })}
            </ul>
            {visible.length < filtered.length ? (
              <Button
                type="button"
                variant="secondary"
                onClick={() => setVisibleCount((current) => current + PAGE_SIZE)}
              >
                Mostrar {Math.min(PAGE_SIZE, filtered.length - visible.length)} más
              </Button>
            ) : null}
          </>
        )}
      </section>
    </div>
  );
}
