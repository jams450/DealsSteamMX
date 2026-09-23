"use client";

// Alta manual de un juego de consola (o de cualquier plataforma) en la biblioteca
// (docs/PLAN_CONSOLE.md §4–§5). El flujo lo manda el servidor: si el título ya existe en el catálogo el
// alta se niega con `candidates` y aquí se elige a qué juego pertenece — nunca se identifica por título
// solo. La portada la escribe el servidor con el id de IGDB elegido en la búsqueda: este componente
// jamás envía una URL.
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Gamepad2, Search, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { normalizeStore } from "@/lib/contracts/stores";
import {
  MANUAL_PLATFORM_OPTIONS,
  MANUAL_PLATFORM_OTHER,
  type ManualAddInput,
  type ManualCandidate,
  type ManualSearchHit
} from "@/lib/contracts/manual-library";
import { addManualGame, deleteManualLibraryRow, searchManualGames } from "../_lib/manual-library-api";

type Props = {
  onClose: () => void;
  /** Se dispara tras un alta o un deshacer: la grilla se recarga fuera de aquí. */
  onAdded: () => void;
};

function hitDescription(hit: ManualSearchHit) {
  const platforms = hit.platforms.map((platform) => platform.name).slice(0, 3).join(", ");
  return [hit.releaseYear !== null ? String(hit.releaseYear) : null, platforms || null].filter(Boolean).join(" · ");
}

export function ManualAddDialog({ onClose, onAdded }: Props) {
  const [title, setTitle] = useState("");
  const [platform, setPlatform] = useState("");
  const [customPlatform, setCustomPlatform] = useState("");
  const [hits, setHits] = useState<ManualSearchHit[] | null>(null);
  const [selectedHit, setSelectedHit] = useState<ManualSearchHit | null>(null);
  const [candidates, setCandidates] = useState<ManualCandidate[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [done, setDone] = useState<{ userLibraryId: number } | null>(null);
  const panelRef = useRef<HTMLElement>(null);

  const store = platform === MANUAL_PLATFORM_OTHER ? normalizeStore(customPlatform.trim()) : platform;

  useEffect(() => {
    panelRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape" && !busy) onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [busy, onClose]);

  // El buscador y el alta comparten el título: cambiarlo invalida lo que se buscó y lo que el servidor
  // ya ofreció como candidatos (son candidatos del texto anterior).
  function onChangeTitle(value: string) {
    setTitle(value);
    setCandidates(null);
    setError(null);
  }

  async function onSearch() {
    const query = title.trim();
    if (query.length === 0 || searching || busy) return;

    setSearching(true);
    setError(null);
    setNotice(null);
    setHits(null);
    setSelectedHit(null);
    try {
      const result = await searchManualGames(query);
      if (result.source === null) {
        setNotice("Búsqueda no disponible (IGDB sin configurar o con error). Puedes escribir el título igual y añadir sin portada.");
      } else {
        setHits(result.hits);
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo buscar en IGDB");
    } finally {
      setSearching(false);
    }
  }

  async function submit(body: ManualAddInput) {
    if (busy) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await addManualGame(body);
      if (result.outcome === "candidates") {
        setCandidates(result.candidates);
        return;
      }
      if (result.outcome === "duplicate") {
        setNotice("Esa fila ya estaba en tu biblioteca: no se duplicó.");
        return;
      }
      setDone({ userLibraryId: result.userLibraryId });
      onAdded();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo añadir el juego");
    } finally {
      setBusy(false);
    }
  }

  function submitForm() {
    const cleanTitle = title.trim();
    if (cleanTitle.length === 0) {
      setError("Escribe el título del juego.");
      return;
    }
    if (!store) {
      setError(platform === MANUAL_PLATFORM_OTHER ? "Escribe tu plataforma en minúsculas, por ejemplo «coleco»." : "Elige una plataforma.");
      return;
    }
    void submit({ store, title: cleanTitle, igdbId: selectedHit?.igdbId });
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    submitForm();
  }

  function attachCandidate(candidate: ManualCandidate) {
    if (!store) return;
    void submit({ store, title: candidate.title, gameId: candidate.gameId });
  }

  function createAnyway() {
    const cleanTitle = title.trim();
    if (!store || cleanTitle.length === 0) return;
    void submit({ store, title: cleanTitle, create: true, igdbId: selectedHit?.igdbId });
  }

  async function onUndo() {
    if (done === null || busy) return;
    setBusy(true);
    setError(null);
    try {
      await deleteManualLibraryRow(done.userLibraryId);
      onAdded();
      onClose();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo deshacer el alta");
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-[70] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm" role="presentation" onClick={onClose}>
      <section
        ref={panelRef}
        tabIndex={-1}
        className="app-card max-h-[90dvh] w-full max-w-xl space-y-4 overflow-y-auto p-5 outline-none"
        role="dialog"
        aria-modal="true"
        aria-labelledby="manual-add-title"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 id="manual-add-title" className="text-lg font-semibold text-primary">
              Añadir juego manual
            </h2>
            <p className="mt-0.5 text-xs text-muted">
              Consolas y cualquier plataforma. Sin precios: la fila entra como «en tu biblioteca».
            </p>
          </div>
          <Button type="button" variant="ghost" onClick={onClose} aria-label="Cerrar">
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </Button>
        </div>

        {error ? <Alert variant="danger">{error}</Alert> : null}
        {notice ? <Alert variant="info">{notice}</Alert> : null}

        {done !== null ? (
          <div className="space-y-3">
            <p className="text-sm text-primary" aria-live="polite">
              Añadido a tu biblioteca.
            </p>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="secondary" loading={busy} onClick={() => void onUndo()}>
                Deshacer
              </Button>
              <Button type="button" variant="primary" onClick={onClose}>
                Cerrar
              </Button>
            </div>
          </div>
        ) : (
          <form className="space-y-4" onSubmit={onSubmit}>
            <div className="space-y-1.5">
              <label htmlFor="manual-add-title-input" className="text-sm font-medium text-primary">
                Título
              </label>
              <input
                id="manual-add-title-input"
                type="text"
                value={title}
                maxLength={256}
                onChange={(event) => onChangeTitle(event.target.value)}
                placeholder="Metroid Dread"
                autoComplete="off"
                className="input-semantic h-10 w-full px-3 text-sm"
              />
              <p className="text-xs text-muted">Como esté en tu colección: sirve el título en español o en inglés.</p>
            </div>

            <div className="flex flex-wrap items-end gap-2">
              <div className="min-w-48 flex-1">
                <Select
                  id="manual-add-platform"
                  label="Plataforma"
                  value={platform}
                  onChange={(event) => {
                    setPlatform(event.target.value);
                    setError(null);
                  }}
                >
                  <option value="" disabled>
                    Elige una plataforma…
                  </option>
                  {MANUAL_PLATFORM_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                  <option value={MANUAL_PLATFORM_OTHER}>Otra plataforma…</option>
                </Select>
              </div>
              <Button type="button" variant="secondary" loading={searching} loadingText="Buscando..." onClick={() => void onSearch()}>
                <Search className="h-4 w-4" aria-hidden="true" />
                Buscar portadas
              </Button>
            </div>

            {platform === MANUAL_PLATFORM_OTHER ? (
              <div className="space-y-1.5">
                <label htmlFor="manual-add-custom-platform" className="text-sm font-medium text-primary">
                  Tu plataforma
                </label>
                <input
                  id="manual-add-custom-platform"
                  type="text"
                  value={customPlatform}
                  maxLength={32}
                  onChange={(event) => setCustomPlatform(event.target.value)}
                  placeholder="coleco"
                  autoComplete="off"
                  className="input-semantic h-10 w-full px-3 text-sm"
                />
                <p className="text-xs text-muted">En minúsculas y sin espacios: letras, números, punto, guion o guion bajo.</p>
              </div>
            ) : null}

            {hits !== null ? (
              <section className="space-y-2" aria-labelledby="manual-add-hits-heading">
                <h3 id="manual-add-hits-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
                  Resultados de IGDB
                </h3>
                {hits.length === 0 ? (
                  <p className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3 text-xs text-muted">
                    Sin resultados: se añadirá con el título tal cual y sin portada.
                  </p>
                ) : (
                  <ul className="space-y-1.5">
                    {hits.map((hit) => {
                      const selected = selectedHit?.igdbId === hit.igdbId;
                      return (
                        <li key={hit.igdbId}>
                          <button
                            type="button"
                            aria-pressed={selected}
                            onClick={() => {
                              setSelectedHit(selected ? null : hit);
                              onChangeTitle(hit.title);
                            }}
                            className={`flex w-full items-center gap-3 rounded-[var(--radius-sm)] border p-2 text-left transition-colors ${
                              selected
                                ? "border-[color:var(--color-border-focus)] bg-[var(--color-surface-3)]"
                                : "border-default bg-[var(--color-surface-2)] hover:bg-[var(--color-surface-3)]"
                            }`}
                          >
                            {hit.imageUrl ? (
                              // eslint-disable-next-line @next/next/no-img-element -- URL de IGDB servida por el backend, no una ruta interna
                              <img src={hit.imageUrl} alt="" className="h-14 w-10 shrink-0 rounded object-cover" loading="lazy" />
                            ) : null}
                            <span className="min-w-0">
                              <span className="block truncate text-sm font-semibold text-primary">{hit.title}</span>
                              <span className="block truncate text-xs text-muted">{hitDescription(hit)}</span>
                            </span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                )}
                {selectedHit ? (
                  <p className="text-xs text-muted" aria-live="polite">
                    Elegido: <span className="font-semibold text-primary">{selectedHit.title}</span>. El servidor traerá su
                    portada y año al añadir.
                  </p>
                ) : null}
              </section>
            ) : null}

            {candidates !== null ? (
              <section className="space-y-2" aria-labelledby="manual-add-candidates-heading">
                <h3 id="manual-add-candidates-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
                  Este título ya existe en el catálogo
                </h3>
                <p className="text-xs text-muted">
                  Elige a qué juego pertenece esta plataforma. No se crea otro registro a menos que lo pidas
                  expresamente.
                </p>
                <ul className="space-y-1.5">
                  {candidates.map((candidate) => (
                    <li key={candidate.gameId} className="flex items-center justify-between gap-3 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-2">
                      <span className="min-w-0">
                        <span className="block truncate text-sm font-semibold text-primary">
                          {candidate.title}
                          {candidate.releaseYear !== null ? ` (${candidate.releaseYear})` : ""}
                        </span>
                        {candidate.inLibrary ? <span className="block text-xs text-muted">Ya tienes una fila de este juego.</span> : null}
                      </span>
                      <Button type="button" variant="secondary" className="h-9 shrink-0 px-3 text-xs" loading={busy} onClick={() => attachCandidate(candidate)}>
                        Añadir aquí
                      </Button>
                    </li>
                  ))}
                </ul>
                <Button type="button" variant="secondary" loading={busy} onClick={createAnyway}>
                  Crear juego nuevo de todas formas
                </Button>
              </section>
            ) : candidates === null && done === null ? (
              <div className="flex flex-wrap items-center gap-3">
                <Button type="button" variant="primary" loading={busy} disabled={title.trim().length === 0 || !store} onClick={submitForm}>
                  <Gamepad2 className="h-4 w-4" aria-hidden="true" />
                  Añadir a la biblioteca
                </Button>
                <span className="text-xs text-muted" aria-live="polite">
                  {busy ? "Añadiendo..." : selectedHit ? "Con portada del resultado elegido." : "Sin portada hasta que busques un resultado."}
                </span>
              </div>
            ) : null}
          </form>
        )}
      </section>
    </div>
  );
}
