"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { Gamepad2 } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import type { SteamSearchResult } from "@/lib/contracts/steam";
import { searchSteam, suggestGames } from "@/app/steam/_lib/steam-api";

const MIN_QUERY_LENGTH = 2;
const SUGGESTION_DEBOUNCE_MS = 300;

const refreshedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

// El normalizador ya descarta fechas inválidas; el guard evita que Intl.format lance si algo se cuela.
function formatRefreshedAt(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : refreshedFormatter.format(date);
}

// Solo los resultados ya comparados llevan etiqueta; si falta la fecha, no se inventa nada.
function ComparedMeta({ result }: { readonly result: SteamSearchResult }) {
  if (!result.hasDetails) return null;

  const refreshed = formatRefreshedAt(result.refreshedAt);

  return (
    <span className="mt-1 flex flex-wrap items-center gap-1.5">
      <span className="tabler-badge tabler-badge-info">Ya comparado</span>
      {refreshed ? <span className="text-xs text-muted">Actualizado {refreshed}</span> : null}
    </span>
  );
}

// 120x45 is the native `tiny_image` size; smaller on phones so the card still fits at 360px.
function SteamThumb({ src, className }: { readonly src: string | null; readonly className?: string }) {
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

export function SearchClient() {
  const searchParams = useSearchParams();
  const [query, setQuery] = useState(searchParams.get("q") ?? "");
  const [results, setResults] = useState<readonly SteamSearchResult[]>([]);
  const [suggestions, setSuggestions] = useState<readonly SteamSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searchedQuery, setSearchedQuery] = useState<string | null>(null);

  const trimmedQuery = query.trim();
  const hasSearched = searchedQuery !== null;

  useEffect(() => {
    // Suggestions stop after an explicit search for the same term; typing again re-enables them.
    if (trimmedQuery.length < MIN_QUERY_LENGTH || trimmedQuery === searchedQuery) {
      setSuggestions([]);
      return;
    }

    const controller = new AbortController();
    const timer = setTimeout(() => {
      suggestGames(trimmedQuery, controller.signal)
        .then((next) => {
          if (!controller.signal.aborted) setSuggestions(next);
        })
        .catch(() => {
          // Suggesting is best effort: a failed lookup must not block the explicit search.
          if (!controller.signal.aborted) setSuggestions([]);
        });
    }, SUGGESTION_DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [trimmedQuery, searchedQuery]);

  async function runSearch(value: string) {
    const q = value.trim();
    setSearchedQuery(q);
    setError(null);

    if (q.length < MIN_QUERY_LENGTH) {
      setError(`Escribe al menos ${MIN_QUERY_LENGTH} caracteres.`);
      setResults([]);
      return;
    }

    setLoading(true);
    try {
      setResults(await searchSteam(q));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo buscar en Steam.");
      setResults([]);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    const q = searchParams.get("q");
    if (q) {
      setQuery(q);
      void runSearch(q);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  return (
    <div className="space-y-4">
      <div className="grid gap-2 sm:grid-cols-[1fr_auto] sm:items-end">
        <Input
          label="Buscar un juego"
          type="search"
          autoComplete="off"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") void runSearch(query);
          }}
          placeholder="Ej. Hades"
        />
        <Button
          type="button"
          loading={loading}
          loadingText="Buscando..."
          className="w-full sm:w-auto"
          onClick={() => void runSearch(query)}
        >
          Buscar
        </Button>
      </div>

      {suggestions.length > 0 ? (
        <section className="app-card overflow-hidden" aria-labelledby="search-suggestions-title">
          <h2
            id="search-suggestions-title"
            className="border-b border-default px-4 py-2 text-xs font-semibold uppercase tracking-widest text-muted"
          >
            Sugerencias
          </h2>
          <ul className="divide-y divide-[var(--color-border)]">
            {suggestions.map((item) => (
              <li key={item.appId}>
                <Link
                  href={`/games/${item.appId}`}
                  className="flex min-h-11 items-center gap-3 px-4 py-2 transition-colors hover:bg-[var(--color-accent-soft)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                >
                  <SteamThumb src={item.imageUrl} className="max-sm:hidden" />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-sm font-semibold text-primary">{item.name}</span>
                    <span className="block text-xs text-muted">
                      AppID {item.appId}{item.type ? ` · ${item.type}` : ""}
                    </span>
                    <ComparedMeta result={item} />
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {error ? (
        <p className="text-sm text-danger" role="alert">{error}</p>
      ) : (
        <div aria-live="polite">
          {loading ? (
            <p className="text-sm text-muted">Cargando...</p>
          ) : hasSearched && results.length === 0 ? (
            <div className="app-card p-8 text-center">
              <p className="text-sm text-muted">Sin resultados</p>
            </div>
          ) : results.length > 0 ? (
            <div className="grid gap-3 md:grid-cols-2">
              {results.map((result) => (
                <Link
                  key={result.appId}
                  href={`/games/${result.appId}`}
                  className="app-card flex items-start gap-3 p-4 transition-colors hover:border-accent"
                >
                  <SteamThumb src={result.imageUrl} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-semibold text-primary">{result.name}</span>
                    <span className="block text-xs text-muted">
                      AppID {result.appId}{result.type ? ` · ${result.type}` : ""}
                    </span>
                    <ComparedMeta result={result} />
                  </span>
                  <span className="hidden shrink-0 self-center text-sm font-semibold text-accent sm:inline">
                    Ver precios
                  </span>
                </Link>
              ))}
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
