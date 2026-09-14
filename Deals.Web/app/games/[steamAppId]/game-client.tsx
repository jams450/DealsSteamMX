"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import type { SteamGame } from "@/lib/contracts/steam";
import { getSteamGame } from "@/app/steam/_lib/steam-api";
import { formatCurrency } from "@/lib/format/currency";

interface GameClientProps {
  readonly appId: number;
}

export function GameClient({ appId }: GameClientProps) {
  const [game, setGame] = useState<SteamGame | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const result = await getSteamGame(appId);
        if (active) setGame(result);
      } catch (cause) {
        if (active) setError(cause instanceof Error ? cause.message : "No se pudo cargar el juego.");
      } finally {
        if (active) setLoading(false);
      }
    }
    void load();
    return () => { active = false; };
  }, [appId]);

  if (loading) return <p className="app-card p-5 text-sm text-muted">Cargando...</p>;

  if (error) {
    return (
      <div className="app-card space-y-3 p-5">
        <p className="text-sm text-danger" role="alert">{error}</p>
        <Link href="/search" className="text-sm text-secondary hover:underline">Volver a resultados</Link>
      </div>
    );
  }

  if (!game) return null;

  const priceDisplay = game.isFree
    ? "Gratis"
    : game.currentPriceMinor !== null && game.currency
      ? formatCurrency(game.currentPriceMinor / 100, "es-MX", game.currency)
      : "Precio no disponible";

  return (
    <div className="space-y-4">
      <Link href="/search" className="text-sm text-secondary hover:underline">Volver a resultados</Link>

      <section className="app-card grid gap-5 p-5 md:grid-cols-[240px_1fr]">
        {game.imageUrl ? (
          <img src={game.imageUrl} alt={`Portada de ${game.name}`} width={460} height={215} className="h-auto w-full rounded-[var(--radius-md)] object-cover" />
        ) : null}
        <div className="space-y-3">
          <p className="text-sm text-muted">Precio Steam en México</p>
          <h2 className="text-2xl font-semibold text-primary">{game.name}</h2>
          <p className="text-sm text-secondary">
            AppID {game.appId}{game.type ? ` · ${game.type}` : ""}
          </p>
          <p className="text-2xl font-bold text-primary">
            {priceDisplay}
            {game.discountPercent ? <span className="ml-2 text-sm font-semibold text-success">-{game.discountPercent}%</span> : null}
          </p>
        </div>
      </section>

      <section className="app-card overflow-x-auto p-1">
        <table className="w-full min-w-[560px] text-left text-sm">
          <caption className="sr-only">Precio de {game.name} en Steam</caption>
          <thead className="text-muted">
            <tr>
              <th className="p-3">Fuente</th>
              <th className="p-3">Precio base</th>
              <th className="p-3">Región</th>
              <th className="p-3">Observado</th>
            </tr>
          </thead>
          <tbody>
            <tr className="table-row">
              <td className="p-3 font-medium text-primary">Steam <span className="tabler-badge tabler-badge-success ml-1">Oficial</span></td>
              <td className="p-3 font-semibold tabular-nums text-primary">
                {game.initialPriceMinor !== null && game.currency
                  ? formatCurrency(game.initialPriceMinor / 100, "es-MX", game.currency)
                  : "—"}
              </td>
              <td className="p-3 font-semibold uppercase text-primary">{game.region ?? "—"}</td>
              <td className="p-3 text-muted">
                {game.observedAt ? new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" }).format(new Date(game.observedAt)) : "—"}
              </td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  );
}
