"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ExternalLink, Gamepad2 } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { cn } from "@/lib/ui/cn";
import type { SteamGame } from "@/lib/contracts/steam";
import { getSteamGame } from "@/app/steam/_lib/steam-api";
import { formatCurrency } from "@/lib/format/currency";

interface GameClientProps {
  readonly appId: number;
}

const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

function steamStoreUrl(appId: number) {
  return `https://store.steampowered.com/app/${appId}/`;
}

function formatMinor(amountMinor: number, currency: string) {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

export function GameClient({ appId }: GameClientProps) {
  const [game, setGame] = useState<SteamGame | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [coverFailed, setCoverFailed] = useState(false);

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
      <div className="space-y-3">
        <Alert variant="danger">{error}</Alert>
        <Link href="/search" className="btn-secondary-semantic inline-flex h-10 items-center px-4 text-sm font-semibold">
          Volver a resultados
        </Link>
      </div>
    );
  }

  if (!game) return null;

  const currency = game.currency;
  const currentPrice = game.currentPriceMinor;
  const hasPrice = currentPrice !== null && currency !== null;
  const priceDisplay = game.isFree
    ? "Gratis"
    : hasPrice
      ? formatMinor(currentPrice, currency)
      : "Precio no disponible";
  // Oferta incompleta: hay precio actual, pero falta el precio base o la región observada.
  const incomplete = hasPrice && (game.initialPriceMinor === null || game.region === null);

  // El mínimo es un dato local, nunca un descuento: sin moneda no se formatea y no se compara.
  const lowestMinor = game.lowestPriceMinor;
  const lowestDisplay = lowestMinor !== null && currency !== null ? formatMinor(lowestMinor, currency) : null;
  const atLowest = lowestDisplay !== null && !game.isFree && currentPrice !== null && lowestMinor !== null && currentPrice <= lowestMinor;
  const lowestDate = game.lowestPriceAt ? observedFormatter.format(new Date(game.lowestPriceAt)) : null;

  const coverUrl = game.imageUrl && !coverFailed ? game.imageUrl : null;
  const storeUrl = steamStoreUrl(game.appId);

  return (
    <div className="space-y-4">
      <Link href="/search" className="btn-secondary-semantic inline-flex h-10 items-center px-4 text-sm font-semibold">
        Volver a resultados
      </Link>

      <section className="app-card-accent flex flex-col gap-5 p-5 md:flex-row md:items-start">
        {coverUrl ? (
          <img
            src={coverUrl}
            alt={`Portada de ${game.name}`}
            width={460}
            height={215}
            decoding="async"
            onError={() => setCoverFailed(true)}
            className="aspect-[460/215] h-auto w-full rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] object-cover md:w-72 md:shrink-0"
          />
        ) : (
          <div
            aria-hidden="true"
            className="flex aspect-[460/215] w-full items-center justify-center rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] text-muted md:w-72 md:shrink-0"
          >
            <Gamepad2 className="h-8 w-8" />
          </div>
        )}
        <div className="min-w-0 space-y-3">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Precio Steam · México</p>
          <h2 className="text-2xl font-semibold tracking-tight text-primary">{game.name}</h2>
          <p className="text-sm text-secondary">
            AppID {game.appId}{game.type ? ` · ${game.type}` : ""}
          </p>
          <div className="flex flex-wrap items-baseline gap-2">
            <p className={cn("deal-price text-3xl", game.isFree ? "text-success" : hasPrice ? "text-primary" : "text-danger")}>
              {priceDisplay}
            </p>
            {game.discountPercent ? (
              <span className="tabler-badge tabler-badge-success">-{game.discountPercent}%</span>
            ) : null}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {game.isFree ? null : hasPrice ? (
              <span className="tabler-badge tabler-badge-success">Precio actual</span>
            ) : (
              <span className="tabler-badge tabler-badge-danger">Sin precio</span>
            )}
            {lowestDisplay ? (
              <span className={cn("tabler-badge", atLowest ? "tabler-badge-success" : "tabler-badge-info")}>
                Mínimo observado localmente {lowestDisplay}{lowestDate ? ` · ${lowestDate}` : ""}
              </span>
            ) : (
              <span className="tabler-badge tabler-badge-muted">Sin mínimo observado localmente</span>
            )}
            {incomplete ? <span className="tabler-badge tabler-badge-warning">Datos incompletos</span> : null}
            {game.observedAt ? (
              <span className="tabler-badge tabler-badge-info">
                Actualizado {observedFormatter.format(new Date(game.observedAt))}
              </span>
            ) : (
              <span className="tabler-badge tabler-badge-warning">Sin fecha de actualización</span>
            )}
          </div>
          <a
            href={storeUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="btn-secondary-semantic inline-flex h-10 items-center gap-2 px-4 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            Ver en Steam
            <ExternalLink className="h-4 w-4" aria-hidden="true" />
            <span className="sr-only">(se abre en una pestaña nueva)</span>
          </a>
        </div>
      </section>

      <section className="table-shell overflow-x-auto">
        <table className="w-full min-w-max text-left text-sm">
          <caption className="sr-only">Precio de {game.name} en Steam y mínimo observado localmente</caption>
          <thead className="table-head">
            <tr>
              <th scope="col" className="p-3">Fuente</th>
              <th scope="col" className="p-3">Precio base</th>
              <th scope="col" className="p-3">Precio con descuento</th>
              <th scope="col" className="p-3">Mínimo observado localmente</th>
              <th scope="col" className="p-3">Región</th>
              <th scope="col" className="p-3">Observado</th>
            </tr>
          </thead>
          <tbody>
            <tr className="table-row">
              <td className="table-cell p-3 font-medium text-primary">
                <a
                  href={storeUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1.5 text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                >
                  Steam
                  <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                  <span className="sr-only">(se abre en una pestaña nueva)</span>
                </a>
                <span className="tabler-badge tabler-badge-success ml-1">Oficial</span>
              </td>
              <td className={cn("table-cell deal-price p-3", game.discountPercent ? "deal-price-strike" : "text-primary")}>
                {game.initialPriceMinor !== null && currency
                  ? formatMinor(game.initialPriceMinor, currency)
                  : "—"}
              </td>
              <td className={cn("table-cell deal-price p-3", game.isFree ? "text-success" : hasPrice ? "text-primary" : "text-muted")}>
                {game.isFree ? "Gratis" : hasPrice ? formatMinor(currentPrice, currency) : "—"}
              </td>
              <td className="table-cell p-3">
                {lowestDisplay ? (
                  <span className="block">
                    <span className="deal-price text-primary">{lowestDisplay}</span>
                    <span className="block text-xs text-muted">{lowestDate ?? "Sin fecha"}</span>
                  </span>
                ) : (
                  <span className="text-muted">—</span>
                )}
              </td>
              <td className="table-cell p-3 font-semibold uppercase text-primary">{game.region ?? "—"}</td>
              <td className="table-cell p-3 text-muted">
                {game.observedAt ? observedFormatter.format(new Date(game.observedAt)) : "—"}
              </td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  );
}
