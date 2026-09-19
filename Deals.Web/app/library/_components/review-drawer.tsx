"use client";

// Ficha lateral de reseñas de un juego. Un juego puede reseñarse tantas veces como se juegue: la lista
// muestra todas las reseñas guardadas de la plataforma elegida y cada una se edita o se borra por
// separado, con un botón aparte para escribir una nueva. Este módulo también expone los helpers que la
// grilla necesita para decidir si un juego se puede reseñar y en qué plataforma abriría: la regla vive en
// un solo lugar y la grilla no la duplica.
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { storeLabel, toStoreKey, type StoreKey } from "@/lib/contracts/stores";
import { formatReviewMonth, newestReview, type Review } from "@/lib/contracts/reviews";
import { createReview, deleteReview, getReviews, updateReview } from "../_lib/reviews-api";
import type { LibraryGame, LibraryState } from "../_lib/library-contract";

// Etiqueta visible de un estado de biblioteca. Vive aquí porque la comparten la columna «Estado» de la
// grilla y el detalle de la plataforma en el drawer: un solo texto para el mismo dato.
export function stateLabel(state: LibraryState): string {
  if (state === "subscription") return "Game Pass";
  return state === "wished" ? "Wishlist" : "En tu biblioteca";
}

// El backend guarda los timestamps en UTC y los manda normalizados a `...Z`; se formatean en UTC para
// que un `Added` de Playnite no cambie de día por la zona del navegador.
const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeZone: "UTC" });

function formatDate(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormatter.format(date);
}

// Una plataforma tal como la ve el drawer: la llave canónica con la que se guardan las reseñas y los datos
// de esa fila que sí cambian por tienda.
export interface ReviewPlatform {
  readonly key: StoreKey;
  readonly label: string;
  readonly state: LibraryState;
  readonly isInstalled: boolean;
  readonly addedAt: string | null;
  readonly review: Review | null;
}

/**
 * Plataformas de un juego en las que se puede escribir reseña, en el orden en que llegaron. Dos filas que
 * normalizan a la misma llave (p. ej. "Ubisoft" y "Ubisoft Connect") son UNA plataforma, porque la reseña
 * se guarda por `(gameId, platform)`. Al fusionar se prefiere la fila que ya trae reseña para no perderla.
 */
export function reviewablePlatforms(game: LibraryGame): readonly ReviewPlatform[] {
  const platforms: ReviewPlatform[] = [];

  for (const platform of game.platforms) {
    const key = toStoreKey(platform.store);
    if (key === null) continue;

    const existingIndex = platforms.findIndex((entry) => entry.key === key);
    const candidate: ReviewPlatform = {
      key,
      label: storeLabel(platform.store),
      state: platform.state,
      isInstalled: platform.isInstalled,
      addedAt: platform.addedAt,
      review: platform.review
    };

    if (existingIndex === -1) {
      platforms.push(candidate);
    } else if (candidate.review !== null) {
      platforms[existingIndex] = candidate;
    }
  }

  return platforms;
}

/**
 * Plataforma con la que se abre el drawer: la reseña más reciente si su plataforma sigue siendo
 * reseñable, si no la primera. `null` significa que la fila no se puede reseñar (sin identidad en el
 * catálogo o sin ninguna tienda del vocabulario) y la grilla no debe ofrecer la acción.
 */
export function defaultReviewPlatform(game: LibraryGame): StoreKey | null {
  if (game.gameId === null) return null;

  const platforms = reviewablePlatforms(game);
  const last = game.lastReview?.platform ?? null;
  const preferred = last === null ? undefined : platforms.find((platform) => platform.key === last);
  return preferred?.key ?? platforms[0]?.key ?? null;
}

// Agrupa las reseñas de un juego por plataforma. Vive fuera del componente porque la usan el estado
// derivado y los manejadores de alta/borrado, que deben recalcular la lista sin perder las demás
// plataformas.
function groupByPlatform(list: readonly Review[]): Map<StoreKey, Review[]> {
  const map = new Map<StoreKey, Review[]>();
  for (const review of list) {
    const bucket = map.get(review.platform);
    if (bucket === undefined) map.set(review.platform, [review]);
    else bucket.push(review);
  }
  return map;
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

// El formulario nace con la reseña elegida (o vacío si es nueva) y se remonta al cambiar de destino
// (`key` en el drawer), así que no necesita sincronizar nada con un `useEffect`.
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
    <form className="drawer-section-semantic space-y-3" onSubmit={(event) => void onSubmit(event)}>
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
          rows={4}
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

// Fila de una reseña guardada: lo que se lee de un vistazo y las acciones de esa reseña concreta.
function SavedReview({
  review,
  busy,
  onEdit,
  onDelete
}: {
  readonly review: Review;
  readonly busy: boolean;
  readonly onEdit: () => void;
  readonly onDelete: () => void;
}) {
  const started = formatReviewMonth(review.startedMonth);
  const finished = formatReviewMonth(review.finishedMonth);
  const range = started === null && finished === null ? null : `${started ?? "?"} → ${finished ?? "en curso"}`;

  return (
    <li className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3">
      <div className="flex flex-wrap items-center gap-2">
        {review.score !== null ? (
          <span className="tabler-badge tabler-badge-primary">
            Nota {review.score}
            {review.scoreLabel ? ` · ${review.scoreLabel}` : ""}
          </span>
        ) : (
          <span className="tabler-badge tabler-badge-muted">Sin nota</span>
        )}
        {review.isGoty ? <span className="tabler-badge tabler-badge-info">GOTY</span> : null}
        <span className="text-xs text-muted">{range ?? "Sin fechas"}</span>
      </div>

      {review.body ? (
        <p className="mt-2 whitespace-pre-wrap break-words text-sm text-secondary">{review.body}</p>
      ) : (
        <p className="mt-2 text-xs text-muted">Sin texto.</p>
      )}

      <div className="mt-2 flex flex-wrap items-center gap-2">
        <Button type="button" variant="secondary" disabled={busy} onClick={onEdit}>
          Editar esta reseña
        </Button>
        <Button type="button" variant="danger" disabled={busy} onClick={onDelete}>
          Borrar
        </Button>
      </div>
    </li>
  );
}

// Destino del formulario: una reseña existente (por id) o una nueva. `null` es "solo la lista".
interface EditorTarget {
  readonly reviewId: number | null;
}

interface ReviewDrawerProps {
  readonly game: LibraryGame;
  readonly onClose: () => void;
  /**
   * Notifica la reseña que representa al juego en la grilla tras un alta, una edición o un borrado: la
   * más reciente que queda, o `null` si ya no hay ninguna. La grilla guarda una sola reseña por fila, así
   * que recibe la representativa y no el evento crudo.
   */
  readonly onReviewsChanged: (gameId: number, platform: StoreKey, review: Review | null) => void;
}

/**
 * Ficha lateral de reseñas. El padre la monta solo cuando hay un juego elegido y con `key={game.key}`, así
 * que cada apertura es un montaje limpio, sin estado heredado de la anterior. La lista completa se pide al
 * endpoint dedicado (`/api/bff/reviews`), no a la fila de biblioteca: esa trae solo la más reciente.
 */
export function ReviewDrawer({ game, onClose, onReviewsChanged }: ReviewDrawerProps) {
  const platforms = useMemo(() => reviewablePlatforms(game), [game]);
  const [platform, setPlatform] = useState<StoreKey | null>(() => defaultReviewPlatform(game));
  // `null` = todavía sin leer (cargando). Un arreglo vacío es "el juego no tiene reseñas": nunca se
  // rellena con lo que traía la fila de biblioteca, porque tras borrar la última eso la resucitaría.
  const [reviews, setReviews] = useState<readonly Review[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<EditorTarget | null>(null);
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const panelRef = useRef<HTMLElement>(null);
  // `onClose` se llama desde un listener global: se guarda en un ref para que el efecto de montaje no se
  // vuelva a enganchar (y devuelva el foco) en cada render del padre.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  const gameId = game.gameId;

  // Escape cierra y el fondo no scrollea mientras la ficha está abierta; al salir se devuelve el scroll
  // y el foco al elemento que la abrió. El foco inicial va al panel (no al primer campo) para que el
  // lector de pantalla anuncie el diálogo completo antes de entrar en el formulario.
  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onCloseRef.current();
    }

    document.addEventListener("keydown", onKeyDown);
    document.body.style.overflow = "hidden";
    panelRef.current?.focus();

    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus();
    };
  }, []);

  // Una sola lectura al abrir: todas las reseñas del juego, de todas sus plataformas. Sin `gameId` no hay
  // nada que pedir (el mismo caso en que la grilla no ofrece la acción).
  useEffect(() => {
    if (gameId === null) return;

    let cancelled = false;
    getReviews(gameId)
      .then((loaded) => {
        if (!cancelled) setReviews(loaded);
      })
      .catch((cause: unknown) => {
        if (cancelled) return;
        // Sin lista no se inventa una: se avisa y el formulario sigue disponible. La grilla conserva la
        // etiqueta que ya tenía.
        setLoadError(cause instanceof Error ? cause.message : "No se pudieron cargar las reseñas.");
        setReviews([]);
      });

    return () => {
      cancelled = true;
    };
  }, [gameId]);

  const byPlatform = useMemo(() => groupByPlatform(reviews ?? []), [reviews]);

  const selected = platforms.find((entry) => entry.key === platform) ?? platforms[0] ?? null;
  const current = selected === null ? [] : (byPlatform.get(selected.key) ?? []);
  const loading = reviews === null;

  const publish = useCallback(
    (list: readonly Review[], key: StoreKey) => {
      if (gameId === null) return;
      onReviewsChanged(gameId, key, newestReview(list));
    },
    [gameId, onReviewsChanged]
  );

  // Al cambiar de plataforma se cierra el formulario: era para la lista anterior.
  useEffect(() => {
    setEditor(null);
    setConfirmingId(null);
    setActionError(null);
  }, [platform]);

  // Alta y edición comparten camino: la reseña guardada reemplaza a la suya en la lista COMPLETA (las
  // demás plataformas no se tocan) y la grilla recibe la representativa de esta plataforma.
  function onSaved(review: Review) {
    const full = [...(reviews ?? []).filter((entry) => entry.reviewId !== review.reviewId), review];
    setReviews(full);
    setEditor(null);
    setActionError(null);
    if (selected !== null) publish(groupByPlatform(full).get(selected.key) ?? [review], selected.key);
  }

  async function onDelete(review: Review) {
    if (deleting || selected === null) return;

    setDeleting(true);
    setActionError(null);
    try {
      await deleteReview(review.reviewId);
      const full = (reviews ?? []).filter((entry) => entry.reviewId !== review.reviewId);
      setReviews(full);
      setConfirmingId(null);
      // El formulario abierto sobre la reseña borrada ya no tiene destino: se cierra.
      setEditor((target) => (target?.reviewId === review.reviewId ? null : target));
      publish(groupByPlatform(full).get(selected.key) ?? [], selected.key);
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : "No se pudo borrar la reseña.");
    } finally {
      setDeleting(false);
    }
  }

  const editing = editor === null ? null : (current.find((entry) => entry.reviewId === editor.reviewId) ?? null);
  const added = selected === null ? null : formatDate(selected.addedAt);
  const title = editor === null ? "Reseñas" : editor.reviewId === null ? "Nueva reseña" : "Editar reseña";

  return (
    <div
      className="fixed inset-0 z-[70] flex items-end justify-end bg-[var(--color-overlay)] backdrop-blur-sm sm:items-stretch"
      role="presentation"
      onClick={onClose}
    >
      <section
        ref={panelRef}
        tabIndex={-1}
        className="app-sidebar relative flex h-[100dvh] w-full flex-col border-l outline-none sm:h-full sm:max-w-xl"
        role="dialog"
        aria-modal="true"
        aria-labelledby="review-drawer-title"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="drawer-header-semantic">
          <div className="mb-1 h-1 w-12 bg-[var(--color-accent)]/70 sm:hidden" />
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 id="review-drawer-title" className="text-lg font-semibold text-primary">
                {title}
              </h2>
              <p className="mt-0.5 truncate text-xs text-muted">{game.title}</p>
            </div>
            <Button type="button" variant="ghost" className="btn-close-semantic" onClick={onClose}>
              <X className="h-3.5 w-3.5" aria-hidden="true" />
              <span>Cerrar</span>
            </Button>
          </div>
        </div>

        <div className="flex-1 space-y-4 overflow-y-auto px-4 py-4 sm:px-5 sm:py-5">
          {selected === null || gameId === null ? (
            // Camino defensivo: la grilla no abre la ficha si no hay plataforma reseñable ni identidad.
            <Alert variant="info">
              Este juego todavía no se puede reseñar: necesita estar identificado en el catálogo y en una
              tienda del vocabulario.
            </Alert>
          ) : (
            <>
              {platforms.length > 1 ? (
                <Select
                  id="review-platform"
                  label="Plataforma"
                  value={selected.key}
                  onChange={(event) => setPlatform(toStoreKey(event.target.value))}
                >
                  {platforms.map((entry) => (
                    <option key={entry.key} value={entry.key}>
                      {entry.label}
                    </option>
                  ))}
                </Select>
              ) : null}

              <p className="text-xs text-muted">
                {stateLabel(selected.state)} · {added ? `Alta ${added}` : "Sin fecha de alta"}
                {selected.isInstalled ? " · Instalado" : ""}
              </p>

              {loadError ? <Alert variant="info">{loadError}</Alert> : null}

              <section className="space-y-2" aria-labelledby="review-saved-heading">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <h3 id="review-saved-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
                    {current.length === 1 ? "1 reseña guardada" : `${current.length} reseñas guardadas`}
                  </h3>
                  {editor === null ? (
                    <Button type="button" variant="primary" onClick={() => setEditor({ reviewId: null })}>
                      Nueva reseña
                    </Button>
                  ) : null}
                </div>

                {loading ? (
                  <p className="text-sm text-muted">Cargando reseñas...</p>
                ) : current.length === 0 ? (
                  <p className="text-sm text-muted">
                    Todavía no hay reseña de {selected.label} para este juego. Cada vez que lo termines puedes
                    agregar una nueva sin perder las anteriores.
                  </p>
                ) : editor === null ? (
                  <ul className="space-y-2">
                    {current.map((review) => (
                      <SavedReview
                        key={review.reviewId}
                        review={review}
                        busy={deleting}
                        onEdit={() => {
                          setConfirmingId(null);
                          setActionError(null);
                          setEditor({ reviewId: review.reviewId });
                        }}
                        onDelete={() => {
                          setActionError(null);
                          setConfirmingId(review.reviewId);
                        }}
                      />
                    ))}
                  </ul>
                ) : null}
              </section>

              {confirmingId !== null ? (
                <div className="drawer-section-semantic space-y-2">
                  <p className="text-xs text-muted">
                    Borrar una reseña no se puede deshacer. Las demás reseñas del juego no se tocan.
                  </p>
                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      type="button"
                      variant="danger"
                      loading={deleting}
                      loadingText="Borrando..."
                      onClick={() => {
                        const target = current.find((entry) => entry.reviewId === confirmingId);
                        if (target !== undefined) void onDelete(target);
                      }}
                    >
                      Sí, borrar
                    </Button>
                    <Button type="button" variant="secondary" disabled={deleting} onClick={() => setConfirmingId(null)}>
                      Cancelar
                    </Button>
                  </div>
                </div>
              ) : null}

              {actionError ? <Alert variant="danger">{actionError}</Alert> : null}

              {editor !== null ? (
                <ReviewEditor
                  key={`${gameId}:${selected.key}:${editor.reviewId ?? "new"}`}
                  domId={game.item.userLibraryId}
                  gameId={gameId}
                  platform={selected.key}
                  existing={editing}
                  onSaved={onSaved}
                  onCancel={() => setEditor(null)}
                />
              ) : null}
            </>
          )}
        </div>
      </section>
    </div>
  );
}
