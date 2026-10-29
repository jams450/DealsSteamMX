"use client";

// Selector de ficha canónica para un ítem del bundle que no tiene identidad (S4). Solo busca: elegir un
// candidato no escribe nada; el id viaja en el cuerpo de la compra, que es el único que escribe.
//
// Por qué este componente guarda su propia búsqueda: el diálogo puede tener varios ítems sin identidad y
// el usuario puede cambiar de objetivo mientras una búsqueda está en vuelo. El estado (texto, resultados,
// error) vive aquí y el componente se monta con `key={itemKey}`, así que la respuesta de un ítem anterior
// no puede pisar el texto ni la selección del actual. Además se aborta la petición anterior y se compara
// un número de secuencia antes de aplicar cada respuesta (`seq`), que es la garantía que un simple
// `AbortController` no da por sí solo: el abort llega tarde cuando el `await` ya se resolvió.
import { useEffect, useRef, useState } from "react";
import { Search } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import type { CatalogCandidate } from "@/lib/contracts/bundle-purchases";
import { searchCatalogCandidates } from "../_lib/bundle-purchases-api";

const SEARCH_DEBOUNCE_MS = 300;

type Props = {
  /** Texto con el que arranca la búsqueda: el título del ítem es una pista editable, no una elección. */
  readonly itemTitle: string;
  readonly onPick: (candidate: CatalogCandidate) => void;
  readonly onCancel: () => void;
};

export function BundlePurchaseLinkSearch({ itemTitle, onPick, onCancel }: Props) {
  const [query, setQuery] = useState(itemTitle);
  const [results, setResults] = useState<readonly CatalogCandidate[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const seq = useRef(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
    inputRef.current?.select();
    return () => {
      // Un componente desmontado no puede aplicar nada: el contador invalida cualquier respuesta tardía.
      seq.current += 1;
    };
  }, []);

  useEffect(() => {
    const text = query.trim();
    const controller = new AbortController();
    const request = ++seq.current;

    if (text.length < 2) {
      setResults(null);
      setSearching(false);
      setError(null);
      return () => controller.abort();
    }

    setSearching(true);
    setError(null);
    const timer = window.setTimeout(() => {
      searchCatalogCandidates(text, controller.signal)
        .then((items) => {
          if (request !== seq.current) return;
          setResults(items);
        })
        .catch((cause: unknown) => {
          if (request !== seq.current || controller.signal.aborted) return;
          setError(cause instanceof Error ? cause.message : "No se pudo buscar en el catálogo.");
        })
        .finally(() => {
          if (request === seq.current) setSearching(false);
        });
    }, SEARCH_DEBOUNCE_MS);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [query]);

  return (
    <section
      aria-labelledby="bundle-link-search-title"
      className="space-y-2 rounded-[var(--radius-md)] border border-accent bg-[var(--color-accent-soft)] p-3"
      onKeyDown={(event) => {
        // Escape cierra el vínculo, no el diálogo: el contenedor se traga el evento antes de que llegue
        // al listener del documento que cierra la compra.
        if (event.key === "Escape") {
          event.preventDefault();
          event.stopPropagation();
          onCancel();
        }
      }}
    >
      <h3 id="bundle-link-search-title" className="text-sm font-semibold text-primary">
        Vincular con una ficha del catálogo
      </h3>
      <p className="text-xs text-muted">
        Busca por título y elige la ficha correcta. No se crea ningún juego nuevo: el ítem queda ligado a
        una ficha que ya existe.
      </p>

      <div className="space-y-1.5">
        <label htmlFor="bundle-link-search-input" className="text-sm font-medium text-primary">
          Buscar en el catálogo
        </label>
        <div className="relative">
          <input
            ref={inputRef}
            id="bundle-link-search-input"
            type="search"
            value={query}
            maxLength={120}
            autoComplete="off"
            onChange={(event) => setQuery(event.target.value)}
            className="input-semantic h-10 w-full pl-9 pr-3 text-sm"
            aria-describedby="bundle-link-search-help"
          />
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" aria-hidden="true" />
        </div>
        <p id="bundle-link-search-help" className="text-xs text-muted">
          Al menos 2 caracteres. Nada se selecciona solo por parecerse al título.
        </p>
      </div>

      {error ? <Alert variant="danger">{error}</Alert> : null}

      <div aria-live="polite" className="text-xs text-muted">
        {searching ? "Buscando en el catálogo..." : ""}
      </div>

      {results !== null ? (
        results.length === 0 ? (
          <p className="text-xs text-secondary">
            {searching ? "" : "Ninguna ficha coincide. Prueba con otro título o con el año."}
          </p>
        ) : (
          <ul className="space-y-1">
            {results.map((candidate) => (
              <li key={candidate.gameId}>
                <button
                  type="button"
                  onClick={() => onPick(candidate)}
                  className="flex w-full items-baseline justify-between gap-2 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface)] px-3 py-2 text-left text-sm text-primary hover:border-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                >
                  <span className="min-w-0">
                    <span className="font-semibold">{candidate.title}</span>
                    {candidate.releaseYear !== null ? (
                      <span className="text-xs text-muted"> · {candidate.releaseYear}</span>
                    ) : null}
                  </span>
                  {candidate.inLibrary ? (
                    <span className="tabler-badge tabler-badge-info shrink-0">En tu biblioteca</span>
                  ) : null}
                </button>
              </li>
            ))}
          </ul>
        )
      ) : null}

      <Button type="button" variant="ghost" onClick={onCancel}>
        Cancelar vínculo
      </Button>
    </section>
  );
}
