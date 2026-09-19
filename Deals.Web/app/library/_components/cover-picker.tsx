"use client";

// Selector de portada de un juego. Busca en Steam por título y el usuario elige el resultado correcto; el
// servidor resuelve y guarda la URL del header de ese appid. Es manual a propósito: la búsqueda por título
// acierta casi siempre, pero no siempre (remakes, ediciones, títulos repetidos), y una portada equivocada
// es visible. Vive fuera de la grilla porque abre encima, no dentro de una celda.
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Search, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import type { SteamSearchResult } from "@/lib/contracts/steam";
import { searchSteam } from "@/app/steam/_lib/steam-api";
import { setGameCover } from "../_lib/library-api";

export function CoverPicker({
  gameId,
  title,
  onClose,
  onPicked
}: {
  readonly gameId: number;
  readonly title: string;
  readonly onClose: () => void;
  readonly onPicked: (gameId: number, imageUrl: string) => void;
}) {
  const [query, setQuery] = useState(title);
  // `null` es "todavía no hay resultados": se distingue de "la búsqueda no encontró nada".
  const [results, setResults] = useState<readonly SteamSearchResult[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [savingAppId, setSavingAppId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  async function runSearch(term: string) {
    const trimmed = term.trim();
    if (trimmed === "") {
      setResults([]);
      return;
    }

    setError(null);
    setSearching(true);
    try {
      setResults(await searchSteam(trimmed));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo buscar en Steam");
    } finally {
      setSearching(false);
    }
  }

  // La búsqueda arranca sola con el título del juego: abrir el selector y encontrar la lista vacía sería
  // un paso de más en el caso normal.
  useEffect(() => {
    void runSearch(title);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    panelRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  async function pick(result: SteamSearchResult) {
    if (savingAppId !== null) return;

    setError(null);
    setSavingAppId(result.appId);
    try {
      onPicked(gameId, await setGameCover(gameId, result.appId));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo guardar la portada");
    } finally {
      setSavingAppId(null);
    }
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    void runSearch(query);
  }

  return (
    <div
      className="fixed inset-0 z-[80] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={onClose}
    >
      <div
        ref={panelRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="cover-picker-title"
        className="app-card flex max-h-[85dvh] w-full max-w-2xl flex-col overflow-hidden outline-none"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3 border-b border-default p-4">
          <div className="min-w-0">
            <h2 id="cover-picker-title" className="text-lg font-semibold text-primary">
              Buscar portada en Steam
            </h2>
            <p className="mt-0.5 truncate text-xs text-muted">{title}</p>
          </div>
          <Button type="button" variant="ghost" className="h-9 w-9 p-0" aria-label="Cerrar" onClick={onClose}>
            <X className="h-4 w-4" aria-hidden="true" />
          </Button>
        </div>

        <form className="flex items-end gap-2 border-b border-default p-4" onSubmit={onSubmit}>
          <div className="flex-1">
            <Input
              id="cover-picker-query"
              label="Título en Steam"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              autoComplete="off"
            />
          </div>
          <Button type="submit" variant="secondary" className="h-10 px-3 text-xs" loading={searching}>
            <Search className="h-3.5 w-3.5" aria-hidden="true" />
            Buscar
          </Button>
        </form>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4">
          {error ? <Alert variant="danger">{error}</Alert> : null}

          {results === null ? (
            <p className="text-sm text-muted">Buscando en Steam...</p>
          ) : results.length === 0 ? (
            <p className="text-sm text-muted">Steam no devolvió resultados para esa búsqueda.</p>
          ) : (
            <ul className="space-y-2">
              {results.map((result) => (
                <li
                  key={result.appId}
                  className="flex items-center gap-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3"
                >
                  <span className="inline-flex h-[45px] w-[120px] shrink-0 items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface)]">
                    {result.imageUrl ? (
                      <img
                        src={result.imageUrl}
                        alt=""
                        width={120}
                        height={45}
                        loading="lazy"
                        decoding="async"
                        className="h-full w-full object-cover"
                      />
                    ) : (
                      <span className="text-[11px] text-muted">Sin imagen</span>
                    )}
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-semibold text-primary">{result.name}</p>
                    <p className="text-xs text-muted">
                      AppID {result.appId}
                      {result.type ? ` · ${result.type}` : ""}
                    </p>
                  </div>
                  <Button
                    type="button"
                    variant="primary"
                    className="h-9 whitespace-nowrap px-3 text-xs"
                    loading={savingAppId === result.appId}
                    onClick={() => void pick(result)}
                  >
                    Usar esta
                  </Button>
                </li>
              ))}
            </ul>
          )}

          <p className="text-xs text-muted">
            La portada se guarda como URL de Steam en el juego del catálogo: aplica a todas las plataformas
            de ese juego y reemplaza la que hubiera.
          </p>
        </div>
      </div>
    </div>
  );
}
