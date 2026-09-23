"use client";

// Editor del título canónico de un juego (`games.title`). Existe porque el nombre canónico es
// **compartido**: la grilla proyecta el mismo título en todas las copias vinculadas al juego, así que
// corregirlo aquí lo corrige en todas a la vez — y nunca toca el `user_library.title` que importó
// Playnite. Dos caminos: escribir el nombre a mano, o buscar el título oficial en IGDB y quedarse con el
// id del resultado correcto (el servidor lee el título y reclama el vínculo; este componente jamás manda
// un título en ese modo). Vive fuera de la grilla porque abre encima, no dentro de una celda.
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Search, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import { storeLabel } from "@/lib/contracts/stores";
import { GAME_TITLE_MAX_LENGTH, type GameTitleEditRequest } from "@/lib/contracts/game-title";
import { type ManualSearchHit } from "@/lib/contracts/manual-library";
import { editGameTitle } from "../_lib/game-title-api";
import { searchManualGames } from "../_lib/manual-library-api";

type Props = {
  readonly gameId: number;
  /** Título canónico que la grilla muestra hoy: el punto de partida de los dos caminos. */
  readonly title: string;
  /** Filas de biblioteca que apuntan a este juego canónico (una por copia/plataforma). */
  readonly linkedRows: number;
  readonly stores: readonly string[];
  readonly onClose: () => void;
  /** Se dispara tras un cambio aplicado: la grilla se recarga entera fuera de aquí. */
  readonly onSaved: () => void;
};

type Mode = "manual" | "igdb";

const MODE_OPTIONS: readonly { readonly value: Mode; readonly label: string }[] = [
  { value: "manual", label: "Escribir el título" },
  { value: "igdb", label: "Buscar el título oficial" }
];

// Contexto de un resultado: año y hasta tres plataformas, para distinguir remakes y ediciones que
// comparten nombre. El título solo no alcanza para elegir bien la fila de IGDB.
function hitDescription(hit: ManualSearchHit) {
  const platforms = hit.platforms.map((platform) => platform.name).slice(0, 3).join(", ");
  return [hit.releaseYear !== null ? String(hit.releaseYear) : null, platforms || null].filter(Boolean).join(" · ");
}

function copiesLabel(count: number) {
  return count === 1 ? "la copia vinculada" : `las ${count} copias vinculadas`;
}

export function TitleEditor({ gameId, title, linkedRows, stores, onClose, onSaved }: Props) {
  const [mode, setMode] = useState<Mode>("manual");
  // Los dos caminos arrancan del título actual: corregir una errata no debería empezar de cero.
  const [manualTitle, setManualTitle] = useState(title);
  const [query, setQuery] = useState(title);
  // `null` es "todavía no se buscó": se distingue de "se buscó y no hubo resultados".
  const [hits, setHits] = useState<readonly ManualSearchHit[] | null>(null);
  const [selectedIgdbId, setSelectedIgdbId] = useState<number | null>(null);
  const [providerUnavailable, setProviderUnavailable] = useState(false);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // El 409 no es un fallo de red: el catálogo rechazó la identidad y no escribió nada. Se cuenta aparte
  // para no leerlo como un error de conexión y para poder mostrar el motivo del servidor tal cual.
  const [conflict, setConflict] = useState<string | null>(null);
  const panelRef = useRef<HTMLElement>(null);

  useEffect(() => {
    panelRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape" && !busy) onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [busy, onClose]);

  async function runSearch(term: string) {
    const trimmed = term.trim();
    if (trimmed === "" || searching || busy) return;

    setSearching(true);
    setError(null);
    setConflict(null);
    setSelectedIgdbId(null);
    try {
      const result = await searchManualGames(trimmed);
      // `source === null` significa «proveedor no disponible», no «sin resultados»: el aviso lo distingue
      // y el camino manual sigue abierto, así que la edición nunca se bloquea por IGDB.
      setProviderUnavailable(result.source === null);
      setHits(result.hits);
    } catch (cause) {
      setHits([]);
      setError(cause instanceof Error ? cause.message : "No se pudo buscar en IGDB");
    } finally {
      setSearching(false);
    }
  }

  // La búsqueda arranca sola la primera vez que se abre el camino oficial: llegar a una lista vacía sería
  // un paso de más en el caso normal. Una búsqueda ya hecha no se repite al volver a la pestaña.
  useEffect(() => {
    if (mode !== "igdb" || hits !== null) return;
    void runSearch(query);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode]);

  async function save(input: GameTitleEditRequest) {
    if (busy || searching) return;

    setBusy(true);
    setError(null);
    setConflict(null);
    try {
      const result = await editGameTitle(gameId, input);
      if (result.kind === "conflict") {
        setConflict(result.reason);
        setBusy(false);
        return;
      }
      // El título es del juego canónico: la grilla entera se recarga para pintar el valor que el servidor
      // guardó en todas las copias.
      onSaved();
      onClose();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo guardar el título");
      setBusy(false);
    }
  }

  function saveManual() {
    const clean = manualTitle.trim();
    if (clean.length === 0) {
      setError("Escribe el título del juego.");
      return;
    }
    void save({ mode: "manual", title: clean });
  }

  function saveIgdb() {
    if (selectedIgdbId === null) return;
    void save({ mode: "igdb", igdbId: selectedIgdbId });
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (busy || searching) return;
    // Enter busca en el camino oficial (el resultado todavía no se ha elegido) y guarda en el manual.
    if (mode === "manual") saveManual();
    else void runSearch(query);
  }

  const canSave = mode === "manual" ? manualTitle.trim().length > 0 : selectedIgdbId !== null;
  const storesText = stores.map((store) => storeLabel(store)).join(", ");
  const sharedNoteId = "title-editor-shared-note";

  return (
    <div
      className="fixed inset-0 z-[80] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={onClose}
    >
      <section
        ref={panelRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="title-editor-heading"
        aria-describedby={sharedNoteId}
        className="app-card flex max-h-[90dvh] w-full max-w-2xl flex-col overflow-hidden outline-none"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3 border-b border-default p-4">
          <div className="min-w-0">
            <h2 id="title-editor-heading" className="text-lg font-semibold text-primary">
              Editar el título del catálogo
            </h2>
            <p className="mt-0.5 truncate text-xs text-muted">Ahora se llama «{title}»</p>
          </div>
          <Button type="button" variant="ghost" className="h-9 w-9 p-0" aria-label="Cerrar" onClick={onClose}>
            <X className="h-4 w-4" aria-hidden="true" />
          </Button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto">
          <div className="space-y-4 p-4">
            {/* El aviso es lo primero y siempre visible: quien edita tiene que saber que el nombre es
                compartido y que el texto importado de Playnite no se toca. */}
            <Alert variant="info" id={sharedNoteId}>
              <p className="font-semibold">Este nombre es el del juego en el catálogo, y es compartido.</p>
              <p className="mt-1">
                Al guardarlo cambia a la vez en {copiesLabel(linkedRows)}
                {storesText ? ` (${storesText})` : ""} que apuntan al mismo juego. No es el título que Playnite
                importó en tu biblioteca, así que un reimport no lo sobrescribe.
              </p>
            </Alert>

            <div className="space-y-1.5">
              <p id="title-editor-mode-label" className="text-sm font-medium text-primary">
                Cómo quieres fijarlo
              </p>
              <div
                className="flex flex-wrap items-center gap-1 border border-strong bg-[var(--color-surface-2)] p-0.5"
                role="group"
                aria-labelledby="title-editor-mode-label"
              >
                {MODE_OPTIONS.map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    aria-pressed={mode === option.value}
                    onClick={() => {
                      setMode(option.value);
                      setError(null);
                      setConflict(null);
                    }}
                    className={cn(
                      "h-9 px-3 text-xs font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                      mode === option.value
                        ? "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]"
                        : "text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary"
                    )}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </div>

            {conflict !== null ? (
              <Alert variant="danger">
                <p className="font-semibold">El catálogo rechazó el cambio y no escribió nada.</p>
                <p className="mt-1">{conflict}</p>
              </Alert>
            ) : null}
            {error ? <Alert variant="danger">{error}</Alert> : null}

            <form className="space-y-4" onSubmit={onSubmit}>
              {mode === "manual" ? (
                <div className="space-y-1.5">
                  <label htmlFor="title-editor-manual" className="text-sm font-medium text-primary">
                    Título del catálogo
                  </label>
                  <input
                    id="title-editor-manual"
                    type="text"
                    value={manualTitle}
                    maxLength={GAME_TITLE_MAX_LENGTH}
                    onChange={(event) => {
                      setManualTitle(event.target.value);
                      setError(null);
                      setConflict(null);
                    }}
                    placeholder="Metroid Dread"
                    autoComplete="off"
                    className="input-semantic h-10 w-full px-3 text-sm"
                  />
                  <p className="text-xs text-muted">
                    Se guarda tal cual (el servidor lo normaliza aparte para comparar). No identifica el juego en
                    ningún proveedor ni toca precios: por eso no cambia las ofertas.
                  </p>
                </div>
              ) : (
                <div className="space-y-3">
                  <div className="flex flex-wrap items-end gap-2">
                    <div className="min-w-48 flex-1 space-y-1.5">
                      <label htmlFor="title-editor-query" className="text-sm font-medium text-primary">
                        Buscar el título oficial
                      </label>
                      <input
                        id="title-editor-query"
                        type="text"
                        value={query}
                        maxLength={GAME_TITLE_MAX_LENGTH}
                        onChange={(event) => {
                          setQuery(event.target.value);
                          setError(null);
                          setConflict(null);
                        }}
                        placeholder="Metroid Dread"
                        autoComplete="off"
                        className="input-semantic h-10 w-full px-3 text-sm"
                      />
                    </div>
                    <Button
                      type="button"
                      variant="secondary"
                      className="h-10 px-3 text-xs"
                      loading={searching}
                      loadingText="Buscando..."
                      onClick={() => void runSearch(query)}
                    >
                      <Search className="h-3.5 w-3.5" aria-hidden="true" />
                      Buscar
                    </Button>
                  </div>

                  {providerUnavailable ? (
                    <Alert variant="info">
                      La búsqueda no está disponible (IGDB sin configurar o con error). Puedes escribir el título
                      a mano y guardarlo igual.
                    </Alert>
                  ) : null}

                  {hits === null ? (
                    <p className="text-sm text-muted">Buscando en IGDB...</p>
                  ) : hits.length === 0 ? (
                    providerUnavailable ? null : (
                      <p className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3 text-xs text-muted">
                        IGDB no devolvió resultados para esa búsqueda. Prueba con otro texto o usa el título a mano.
                      </p>
                    )
                  ) : (
                    <section className="space-y-2" aria-labelledby="title-editor-hits-heading">
                      <h3
                        id="title-editor-hits-heading"
                        className="text-xs font-semibold uppercase tracking-widest text-muted"
                      >
                        Resultados de IGDB ({hits.length})
                      </h3>
                      <ul className="space-y-1.5">
                        {hits.map((hit) => {
                          const selected = selectedIgdbId === hit.igdbId;
                          return (
                            <li key={hit.igdbId}>
                              <button
                                type="button"
                                aria-pressed={selected}
                                onClick={() => {
                                  setSelectedIgdbId(selected ? null : hit.igdbId);
                                  setError(null);
                                  setConflict(null);
                                }}
                                className={cn(
                                  "flex w-full items-center gap-3 rounded-[var(--radius-sm)] border p-2 text-left transition-colors",
                                  selected
                                    ? "border-[color:var(--color-border-focus)] bg-[var(--color-surface-3)]"
                                    : "border-default bg-[var(--color-surface-2)] hover:bg-[var(--color-surface-3)]"
                                )}
                              >
                                {hit.imageUrl ? (
                                  // eslint-disable-next-line @next/next/no-img-element -- URL de IGDB servida por el backend, no una ruta interna
                                  <img
                                    src={hit.imageUrl}
                                    alt=""
                                    className="h-14 w-10 shrink-0 rounded object-cover"
                                    loading="lazy"
                                    decoding="async"
                                  />
                                ) : (
                                  <span className="inline-flex h-14 w-10 shrink-0 items-center justify-center rounded border border-default bg-[var(--color-surface)] text-[10px] text-muted">
                                    Sin imagen
                                  </span>
                                )}
                                <span className="min-w-0">
                                  <span className="block truncate text-sm font-semibold text-primary">
                                    {hit.title}
                                  </span>
                                  <span className="block truncate text-xs text-muted">{hitDescription(hit)}</span>
                                </span>
                              </button>
                            </li>
                          );
                        })}
                      </ul>
                    </section>
                  )}

                  {selectedIgdbId !== null ? (
                    <p className="text-xs text-muted" aria-live="polite">
                      Elegido:{" "}
                      <span className="font-semibold text-primary">
                        {hits?.find((hit) => hit.igdbId === selectedIgdbId)?.title ?? "resultado"}
                      </span>
                      . El servidor leerá su título y vinculará el id de IGDB a este juego. Si ese id ya
                      pertenece a otro juego del catálogo, el cambio se rechaza sin escribir nada.
                    </p>
                  ) : null}

                  <p className="text-xs text-muted">
                    Buscar el título oficial no cambia precios ni ofertas por sí solo: solo fija el nombre y
                    reclama el vínculo con IGDB de ese resultado.
                  </p>
                </div>
              )}
            </form>
          </div>
        </div>

        <div className="flex flex-wrap items-center justify-end gap-2 border-t border-default p-4">
          <Button type="button" variant="secondary" onClick={onClose} disabled={busy}>
            Cancelar
          </Button>
          <Button
            type="button"
            variant="primary"
            loading={busy}
            loadingText="Guardando..."
            disabled={!canSave}
            onClick={mode === "manual" ? saveManual : saveIgdb}
          >
            {mode === "manual" ? "Guardar título" : "Guardar título oficial"}
          </Button>
        </div>
      </section>
    </div>
  );
}
