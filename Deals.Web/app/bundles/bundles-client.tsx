"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ExternalLink } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { formatCurrency } from "@/lib/format/currency";
import type {
  BundleAllocationStatus,
  BundleCatalog,
  BundleCatalogItem,
  BundleCatalogTier
} from "@/lib/contracts/bundles";
import { getBundleCatalog } from "./_lib/bundles-api";

const ITAD_ATTRIBUTION_URL = "https://isthereanydeal.com";
const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

function formatMinor(amountMinor: number, currency: string) {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

function formatObserved(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : observedFormatter.format(date);
}

// `allocationStatus` es un código de máquina; el copy vive aquí. Un tier sin reparto asignable muestra
// el motivo y ninguna cifra, nunca un cero que se lea como precio.
const ALLOCATION_REASON_LABELS: Record<Exclude<BundleAllocationStatus, "ok">, string> = {
  addon: "Tier addon: sin reparto por juego",
  items_incomplete: "ITAD no listó el contenido completo: sin reparto",
  no_tier_price: "ITAD no publica un precio único (posible build your own): sin reparto",
  currency_unknown: "El precio no trae moneda: sin reparto",
  no_eligible_items: "El tier no lista juegos: sin reparto"
};

function AllocationLine({
  amountMinor,
  currency,
  eligibleGameCount
}: {
  readonly amountMinor: number;
  readonly currency: string;
  readonly eligibleGameCount: number;
}) {
  return (
    <p className="text-xs text-secondary">
      Reparto equitativo estimado: <span className="font-semibold text-primary">{formatMinor(amountMinor, currency)}</span> por
      juego · {eligibleGameCount === 1 ? "1 juego elegible" : `${eligibleGameCount} juegos elegibles`} · requiere
      comprar el tier completo; no es el precio de un juego suelto.
    </p>
  );
}

function TierRow({ tier }: { readonly tier: BundleCatalogTier }) {
  const priceDisplay =
    tier.priceMinor === null || tier.currency === null
      ? "Sin precio en ITAD"
      : tier.priceMinor === 0
        ? "Gratis"
        : formatMinor(tier.priceMinor, tier.currency);
  const hasPrice = tier.priceMinor !== null && tier.currency !== null;
  const reason = tier.allocationStatus === "ok" ? null : ALLOCATION_REASON_LABELS[tier.allocationStatus];

  return (
    <li className="space-y-1">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className="text-xs text-muted">Precio del tier</span>
        <span className={hasPrice ? "deal-price text-primary" : "deal-price text-muted"}>{priceDisplay}</span>
        {tier.currency ? (
          <span className="text-xs font-semibold uppercase text-secondary">{tier.currency}</span>
        ) : null}
        {tier.addon ? <span className="tabler-badge tabler-badge-warning">Addon</span> : null}
        {!tier.itemsComplete ? (
          <span className="tabler-badge tabler-badge-warning">Contenido incompleto</span>
        ) : null}
      </div>

      {tier.allocationStatus === "ok" && tier.allocatedPerGameMinor !== null && tier.currency !== null ? (
        <AllocationLine
          amountMinor={tier.allocatedPerGameMinor}
          currency={tier.currency}
          eligibleGameCount={tier.eligibleGameCount}
        />
      ) : null}
      {reason ? <p className="text-xs text-muted">{reason}</p> : null}
    </li>
  );
}

function BundleCard({ item }: { readonly item: BundleCatalogItem }) {
  const href = item.dealUrl ?? item.pageUrl;
  const published = formatObserved(item.publishedAt);
  const expires = formatObserved(item.expiresAt);
  const observed = formatObserved(item.observedAt);

  return (
    <article className="app-card space-y-3 p-4">
      <div className="space-y-1">
        <div className="flex flex-wrap items-center gap-1.5">
          {href ? (
            <a
              href={href}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 text-base font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              {item.title}
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              <span className="sr-only">(se abre en una pestaña nueva)</span>
            </a>
          ) : (
            <span className="text-base font-semibold text-primary">{item.title}</span>
          )}
          <span className="tabler-badge tabler-badge-info">ITAD</span>
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
          {item.shopName ? <span className="font-semibold text-secondary">{item.shopName}</span> : null}
          {published ? <span>Publicado {published}</span> : null}
          {expires ? <span>Expira {expires}</span> : null}
          {observed ? <span>Observado {observed}</span> : null}
        </div>
      </div>

      {item.details ? <p className="text-xs text-secondary">{item.details}</p> : null}

      <div className="space-y-1">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">
          Juegos ligados observados {item.linkedGames.length > 0 ? `(${item.linkedGames.length})` : ""}
        </p>
        {item.linkedGames.length > 0 ? (
          <ul className="flex flex-wrap gap-1">
            {item.linkedGames.map((game) => (
              <li key={game.appId}>
                <Link
                  href={`/games/${game.appId}`}
                  className="tabler-badge tabler-badge-muted transition-colors hover:border-accent hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                >
                  {game.name}
                </Link>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-xs text-muted">
            Ningún juego de este bundle se ha observado en el catálogo todavía. ITAD solo relaciona un
            bundle con los juegos que se le pidieron.
          </p>
        )}
      </div>

      {item.tiers.length > 0 ? (
        <ul className="space-y-3 border-t border-default pt-3">
          {item.tiers.map((tier, index) => (
            <TierRow key={index} tier={tier} />
          ))}
        </ul>
      ) : (
        <p className="tabler-badge tabler-badge-muted">Sin tiers publicados por el proveedor</p>
      )}
    </article>
  );
}

export function BundlesClient() {
  const [catalog, setCatalog] = useState<BundleCatalog | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setCatalog(await getBundleCatalog());
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo cargar el catálogo de bundles.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (loading) return <p className="app-card p-5 text-sm text-muted">Cargando...</p>;
  if (error) {
    return (
      <div className="space-y-3">
        <Alert variant="danger">{error}</Alert>
        <Button type="button" variant="secondary" onClick={() => void load()}>
          Reintentar
        </Button>
      </div>
    );
  }
  if (catalog === null) return null;

  return (
    <div className="space-y-4">
      <section className="app-card space-y-1 p-5" aria-labelledby="bundles-heading">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">Catálogo</p>
        <h2 id="bundles-heading" className="text-xl font-semibold tracking-tight text-primary">
          Bundles activos
        </h2>
        <p className="text-xs text-muted">
          Paquetes que ITAD reportó y que ya están guardados; abrir esta página no consulta al proveedor.
          El reparto por juego es un estimado: divide el precio del tier entre sus juegos elegibles y solo
          aplica si compras el tier completo. No es el precio de ningún juego suelto y no entra en el
          comparador de precios.
        </p>
        <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-sm font-semibold text-secondary">
          <span>Datos de bundles:</span>
          <a
            href={ITAD_ATTRIBUTION_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            IsThereAnyDeal
          </a>
        </p>
        <div className="flex flex-wrap items-center gap-2 pt-1">
          <span className="tabler-badge tabler-badge-muted">
            {catalog.count === 1 ? "1 bundle" : `${catalog.count} bundles`}
          </span>
          {catalog.maxItems > 0 && catalog.count >= catalog.maxItems ? (
            <span className="tabler-badge tabler-badge-warning">
              Se muestran hasta {catalog.maxItems}; puede haber más
            </span>
          ) : null}
        </div>
      </section>

      {catalog.items.length > 0 ? (
        <div className="grid gap-3 xl:grid-cols-2">
          {catalog.items.map((item) => (
            <BundleCard key={`${item.source}:${item.bundleKey}`} item={item} />
          ))}
        </div>
      ) : (
        <p className="app-card p-5 text-sm text-muted">No hay bundles activos guardados por ahora.</p>
      )}
    </div>
  );
}
