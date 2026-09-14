"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import type { SteamSearchResult } from "@/lib/contracts/steam";
import { searchSteam } from "@/app/steam/_lib/steam-api";

export function SearchClient() {
  const searchParams = useSearchParams();
  const [query, setQuery] = useState(searchParams.get("q") ?? "");
  const [results, setResults] = useState<readonly SteamSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);

  async function runSearch(value: string) {
    const q = value.trim();
    if (q.length < 2) {
      setError("Escribe al menos 2 caracteres.");
      setResults([]);
      setSearched(true);
      return;
    }
    setLoading(true);
    setError(null);
    try {
      setResults(await searchSteam(q));
      setSearched(true);
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
      <div className="flex gap-2">
        <div className="flex-1">
          <Input
            label="Buscar un juego"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter") void runSearch(query);
            }}
            placeholder="Ej. Hades"
          />
        </div>
        <Button
          type="button"
          loading={loading}
          loadingText="Buscando..."
          className="mt-6"
          onClick={() => void runSearch(query)}
        >
          Buscar
        </Button>
      </div>

      {error ? (
        <p className="text-sm text-danger" role="alert">{error}</p>
      ) : loading ? (
        <p className="text-sm text-muted">Cargando...</p>
      ) : searched && results.length === 0 ? (
        <div className="app-card p-8 text-center">
          <p className="text-sm text-muted">Sin resultados</p>
        </div>
      ) : results.length > 0 ? (
        <div className="grid gap-3 md:grid-cols-2">
          {results.map((result) => (
            <Link
              key={result.appId}
              href={`/games/${result.appId}`}
              className="app-card flex items-start justify-between gap-3 p-4 transition-colors hover:border-accent"
            >
              <div className="min-w-0">
                <h2 className="truncate font-semibold text-primary">{result.name}</h2>
                <p className="text-xs text-muted">
                  AppID {result.appId}{result.type ? ` · ${result.type}` : ""}
                </p>
              </div>
              <span className="shrink-0 text-sm font-semibold text-accent">Ver precios</span>
            </Link>
          ))}
        </div>
      ) : null}
    </div>
  );
}
