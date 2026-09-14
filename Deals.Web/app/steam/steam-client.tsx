"use client";

import { useState } from "react";
import { AdminShell } from "@/components/navigation/admin-shell";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { formatCurrency } from "@/lib/format/currency";
import type { SteamGame, SteamSearchResult } from "@/lib/contracts/steam";
import { getSteamGame, searchSteam } from "./_lib/steam-api";

type Props = { username: string };

export function SteamClient({ username }: Props) {
  const [query, setQuery] = useState("");
  const [appId, setAppId] = useState("");
  const [results, setResults] = useState<readonly SteamSearchResult[]>([]);
  const [game, setGame] = useState<SteamGame | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function searchByName() {
    const value = query.trim();
    if (value.length < 2) {
      setError("Escribe al menos 2 caracteres.");
      return;
    }
    setLoading(true);
    setError(null);
    setGame(null);
    try {
      setResults(await searchSteam(value));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo buscar en Steam.");
      setResults([]);
    } finally {
      setLoading(false);
    }
  }

  async function loadGame(value: number) {
    setLoading(true);
    setError(null);
    try {
      setGame(await getSteamGame(value));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo cargar el juego.");
      setGame(null);
    } finally {
      setLoading(false);
    }
  }

  async function searchByAppId() {
    const value = Number(appId.trim());
    if (!Number.isSafeInteger(value) || value <= 0) {
      setError("El AppID debe ser un número positivo.");
      return;
    }
    setResults([]);
    await loadGame(value);
  }

  const price = game?.currentPriceMinor === null || game?.currentPriceMinor === undefined || !game.currency
    ? "Precio no disponible"
    : formatCurrency(game.currentPriceMinor / 100, "es-MX", game.currency);

  return (
    <AdminShell username={username} section="Catálogo" title="Steam" subtitle="Busca juegos y consulta su precio regional en México.">
      <div className="space-y-4">
        <Card className="p-4 sm:p-5">
          <div className="grid gap-4 lg:grid-cols-[1fr_auto_1fr_auto] lg:items-end">
            <label className="space-y-1.5">
              <span className="text-sm font-semibold text-primary">Nombre del juego</span>
              <input className="h-10 w-full rounded-[var(--radius-sm)] border border-strong bg-[var(--color-surface-1)] px-3 text-sm text-primary outline-none focus:border-[var(--color-border-focus)]" value={query} onChange={(event) => setQuery(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") void searchByName(); }} placeholder="Ej. Hades" />
            </label>
            <Button type="button" loading={loading} loadingText="Buscando..." onClick={() => void searchByName()}>Buscar</Button>
            <label className="space-y-1.5">
              <span className="text-sm font-semibold text-primary">Steam AppID</span>
              <input inputMode="numeric" className="h-10 w-full rounded-[var(--radius-sm)] border border-strong bg-[var(--color-surface-1)] px-3 text-sm text-primary outline-none focus:border-[var(--color-border-focus)]" value={appId} onChange={(event) => setAppId(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") void searchByAppId(); }} placeholder="Ej. 1145360" />
            </label>
            <Button type="button" variant="secondary" loading={loading} loadingText="Cargando..." onClick={() => void searchByAppId()}>Consultar</Button>
          </div>
          <p className="mt-3 text-xs text-muted" aria-live="polite">{error ?? "Los precios se guardan en MXN y se actualizan desde Steam."}</p>
        </Card>

        {results.length > 0 ? (
          <Card className="p-4 sm:p-5">
            <h2 className="text-base font-semibold text-primary">Resultados</h2>
            <ul className="mt-3 divide-y divide-[var(--color-border)]">
              {results.map((result) => (
                <li key={result.appId} className="flex items-center justify-between gap-3 py-3">
                  <div className="min-w-0"><p className="truncate text-sm font-semibold text-primary">{result.name}</p><p className="text-xs text-muted">AppID {result.appId}{result.type ? ` · ${result.type}` : ""}</p></div>
                  <Button type="button" variant="ghost" onClick={() => void loadGame(result.appId)}>Ver precio</Button>
                </li>
              ))}
            </ul>
          </Card>
        ) : null}

        {game ? (
          <Card className="p-4 sm:p-5">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div><p className="text-xs font-semibold uppercase tracking-wide text-muted">Juego Steam</p><h2 className="mt-1 text-xl font-semibold text-primary">{game.name}</h2><p className="mt-1 text-sm text-muted">AppID {game.appId}{game.type ? ` · ${game.type}` : ""}</p></div>
              <div className="text-right"><p className="text-2xl font-bold text-primary">{game.isFree ? "Gratis" : price}</p>{game.discountPercent ? <p className="text-sm font-semibold text-success">-{game.discountPercent}%</p> : null}</div>
            </div>
            <dl className="mt-5 grid gap-3 text-sm sm:grid-cols-3"><div><dt className="text-muted">Precio base</dt><dd className="font-semibold text-primary">{game.initialPriceMinor === null || !game.currency ? "No disponible" : formatCurrency(game.initialPriceMinor / 100, "es-MX", game.currency)}</dd></div><div><dt className="text-muted">Región</dt><dd className="font-semibold uppercase text-primary">{game.region}</dd></div><div><dt className="text-muted">Actualizado</dt><dd className="font-semibold text-primary">{game.observedAt ? new Date(game.observedAt).toLocaleString("es-MX") : "No disponible"}</dd></div></dl>
          </Card>
        ) : null}
      </div>
    </AdminShell>
  );
}
