"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, ExternalLink, Heart, ListPlus } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { formatCurrency } from "@/lib/format/currency";
import {
  BUNDLE_PAGE_SIZE_DEFAULT,
  GGDEALS_SOURCE,
  type BundleAllocationStatus,
  type BundleCatalogItem,
  type BundleCatalogTier
} from "@/lib/contracts/bundles";
import {
  allocatedPerGameMoneyView,
  fxDisclosureText,
  tierMoneyView,
  type DualMoneyView
} from "./_lib/bundle-money";
import { getBundleCatalog } from "./_lib/bundles-api";

const ITAD_ATTRIBUTION_URL = "https://isthereanydeal.com";
const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

function formatMinor(amountMinor: number, currency: string) {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

function currencyColor(currency: string | null) {
  const code = currency?.toUpperCase();
  return code === "USD" ? "text-success" : code === "MXN" ? "text-accent" : "text-primary";
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
      Reparto equitativo estimado:{" "}
      <span className={`font-semibold ${currencyColor(currency)}`}>{formatMinor(amountMinor, currency)}</span> por juego ·{" "}
      {eligibleGameCount === 1 ? "1 juego elegible" : `${eligibleGameCount} juegos elegibles`} · requiere
      comprar el tier completo; no es el precio de un juego suelto.
    </p>
  );
}

// Filas compactas de la moneda que la línea nativa no muestra. El `≈` lo decide `bundle-money` a partir
// de la moneda nativa del tier, nunca una multiplicación aquí: el importe es el que mandó el servidor.
function DualMoneyRows({ view, label }: { readonly view: DualMoneyView; readonly label: string }) {
  // La etiqueta solo acompaña a una cifra: si no hay ninguna conversión disponible, la fila se explica
  // sola con «no disponible» y no promete un equivalente que no existe.
  const hasVisibleAmount = view.displayLegs.some((leg) => leg.amountMinor !== null);

  return (
    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1 text-xs">
      {hasVisibleAmount ? <span className="text-muted">{label}</span> : null}
      {view.displayLegs.map((leg) => (
        <span key={leg.currency} className="inline-flex items-baseline gap-1">
          {leg.amountMinor === null ? (
            <span className="text-muted">{leg.currency} no disponible</span>
          ) : (
            <>
              <span className={`font-semibold ${currencyColor(leg.currency)}`}>
                {leg.approximate ? "≈ " : ""}
                {formatMinor(leg.amountMinor, leg.currency)}
              </span>
              <span className={`text-xs font-semibold uppercase ${currencyColor(leg.currency)}`}>{leg.currency}</span>
            </>
          )}
        </span>
      ))}
    </div>
  );
}

function TierRow({ tier, index }: { readonly tier: BundleCatalogTier; readonly index: number }) {
  const priceDisplay =
    tier.priceMinor === null || tier.currency === null
      ? "Sin precio en ITAD"
      : tier.priceMinor === 0
        ? "Gratis"
        : formatMinor(tier.priceMinor, tier.currency);
  const hasPrice = tier.priceMinor !== null && tier.currency !== null;
  const reason = tier.allocationStatus === "ok" ? null : ALLOCATION_REASON_LABELS[tier.allocationStatus];
  // Filas duales desde el módulo puro: sin precio nativo no hay filas, y la moneda nativa no se repite.
  const money = tierMoneyView(tier);
  const allocatedMoney = allocatedPerGameMoneyView(tier);
  const fxNotes = [money, allocatedMoney]
    .map((view) => fxDisclosureText(view?.fx ?? null, view?.nativeCurrency ?? null))
    .filter((note, index, all): note is string => note !== null && all.indexOf(note) === index);

  return (
    <li className="space-y-2 rounded-[var(--radius-md)] border border-default p-3">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-sm font-semibold text-primary">Tier {index + 1}</span>
          {tier.addon ? <span className="tabler-badge tabler-badge-warning">Addon</span> : null}
          {!tier.itemsComplete ? (
            <span className="tabler-badge tabler-badge-warning">Contenido incompleto</span>
          ) : null}
        </div>
        <div className="flex items-baseline gap-2">
          <span className={hasPrice ? `deal-price text-lg ${currencyColor(tier.currency)}` : "deal-price text-lg text-muted"}>
            {priceDisplay}
          </span>
          {tier.currency ? (
            <span className={`text-xs font-semibold uppercase ${currencyColor(tier.currency)}`}>{tier.currency}</span>
          ) : null}
        </div>
      </div>

      <p className="text-xs text-muted">
        {tier.itemCount === 1 ? "1 ítem listado" : `${tier.itemCount} ítems listados`}
        {tier.eligibleGameCount > 0
          ? ` · ${tier.eligibleGameCount === 1 ? "1 juego elegible" : `${tier.eligibleGameCount} juegos elegibles`}`
          : " · sin juegos elegibles"}
      </p>

      {tier.selectionCount !== null ? (
        <p className="text-xs text-secondary">
          Eliges {tier.selectionCount} de {tier.itemCount} ítems; el precio es del tier completo.
        </p>
      ) : null}

      {money ? <DualMoneyRows view={money} label="Equivalente aproximado:" /> : null}

      {tier.allocationStatus === "ok" && tier.allocatedPerGameMinor !== null && tier.currency !== null ? (
        <AllocationLine
          amountMinor={tier.allocatedPerGameMinor}
          currency={tier.currency}
          eligibleGameCount={tier.eligibleGameCount}
        />
      ) : null}
      {allocatedMoney ? <DualMoneyRows view={allocatedMoney} label="Reparto convertido por juego:" /> : null}
      {fxNotes.map((note) => (
        <p key={note} className="text-xs text-muted">
          ≈ Conversión estimada: {note}
        </p>
      ))}
      {reason ? <p className="text-xs text-muted">{reason}</p> : null}
    </li>
  );
}

// El acento marca coincidencia con la wishlist; una tarjeta normal no lo lleva. El color nunca comunica
// solo: la tarjeta marcada lleva además la etiqueta textual «En tu wishlist».
function WishlistHighlight({ item }: { readonly item: BundleCatalogItem }) {
  if (!item.hasWishlistMatch) {
    return null;
  }

  return (
    <div className="space-y-1.5 rounded-[var(--radius-md)] border border-accent bg-[var(--color-accent-soft)] p-2.5">
      <p className="flex items-center gap-1.5 text-xs font-semibold text-primary">
        <Heart className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
        En tu wishlist
      </p>
      {item.wishlistMatches.length > 0 ? (
        <ul className="flex flex-wrap gap-1">
          {item.wishlistMatches.map((match) => (
            <li key={match.appId}>
              <Link
                href={`/games/${match.appId}`}
                className="tabler-badge tabler-badge-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
              >
                {match.name}
              </Link>
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-xs text-secondary">Este bundle incluye un juego de tu wishlist.</p>
      )}
    </div>
  );
}

// Las URLs validadas se usan como respaldo del enlace del título, sin repetirlas debajo.
// Los detalles descriptivos se conservan como texto plano del proveedor.
function BundleDetails({ item }: { readonly item: BundleCatalogItem }) {
  if (!item.details || item.detailsUrl) return null;
  return <p className="text-xs text-secondary">{item.details}</p>;
}

function BundleCard({ item }: { readonly item: BundleCatalogItem }) {
  const href = item.dealUrl ?? item.pageUrl ?? item.detailsUrl;
  const published = formatObserved(item.publishedAt);
  const expires = formatObserved(item.expiresAt);
  const observed = formatObserved(item.observedAt);
  const isGgDeals = item.source === GGDEALS_SOURCE;

  return (
    <article
      className={item.hasWishlistMatch ? "app-card app-card-accent space-y-3 p-4" : "app-card space-y-3 p-4"}
    >
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
          <span className={isGgDeals ? "tabler-badge tabler-badge-warning" : "tabler-badge tabler-badge-info"}>
            {isGgDeals ? "GG.deals" : "ITAD"}
          </span>
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
          {item.shopName ? <span className="font-semibold text-secondary">{item.shopName}</span> : null}
          {published ? <span>Publicado {published}</span> : null}
          {expires ? <span>Expira {expires}</span> : null}
          {observed ? <span>Observado {observed}</span> : null}
        </div>
        {isGgDeals ? (
          <p className="text-xs text-muted">
            Precio de referencia de la región configurada en gg.deals; no acredita disponibilidad en México y esto no
            acredita disponibilidad ni precio final para MX.
          </p>
        ) : null}
      </div>

      <WishlistHighlight item={item} />

      {item.details ? <BundleDetails item={item} /> : null}

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
        <div className="space-y-2 border-t border-default pt-3">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Tiers y precios</p>
          <ul className="space-y-2">
            {item.tiers.map((tier, index) => (
              <TierRow key={index} tier={tier} index={index} />
            ))}
          </ul>
        </div>
      ) : (
        <p className="tabler-badge tabler-badge-muted">Sin tiers publicados por el proveedor</p>
      )}
    </article>
  );
}

type LoadMode = "replace" | "append";

export function BundlesClient() {
  const [items, setItems] = useState<readonly BundleCatalogItem[]>([]);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(BUNDLE_PAGE_SIZE_DEFAULT);
  const [total, setTotal] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);

  const load = useCallback(
    async (targetPage: number, mode: LoadMode) => {
      const request = ++requestId.current;
      setLoading(true);
      setError(null);
      try {
        const result = await getBundleCatalog(targetPage, pageSize);
        if (request !== requestId.current) return;
        setPage(result.page);
        setPageSize(result.pageSize);
        setTotal(result.total);
        setHasMore(result.hasMore);
        setItems((previous) => {
          if (mode !== "append") return result.items;
          const seen = new Set(previous.map((item) => `${item.source}:${item.bundleKey}`));
          const merged = [...previous];
          for (const item of result.items) {
            const key = `${item.source}:${item.bundleKey}`;
            if (seen.has(key)) continue;
            seen.add(key);
            merged.push(item);
          }
          return merged;
        });
      } catch (cause) {
        if (request !== requestId.current) return;
        // Un error no borra lo que ya está en pantalla: la página actual se conserva y se ofrece reintentar.
        setError(cause instanceof Error ? cause.message : "No se pudo cargar el catálogo de bundles.");
      } finally {
        if (request === requestId.current) setLoading(false);
      }
    },
    [pageSize]
  );

  useEffect(() => {
    void load(1, "replace");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const wishlistCount = items.filter((item) => item.hasWishlistMatch).length;
  const isInitialLoading = loading && items.length === 0 && error === null;

  return (
    <div className="space-y-4">
      <section className="app-card space-y-1 p-5" aria-labelledby="bundles-heading">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">Catálogo</p>
        <h2 id="bundles-heading" className="text-xl font-semibold tracking-tight text-primary">
          Bundles activos
        </h2>
        <p className="text-xs text-muted">
          Paquetes que ITAD reportó y que ya están guardados; abrir esta página no consulta al proveedor.
          Se muestran todos los bundles, tengan o no juegos de tu wishlist. El reparto por juego es un
          estimado: divide el precio del tier entre sus juegos elegibles y solo aplica si compras el tier
          completo. No es el precio de ningún juego suelto y no entra en el comparador de precios.
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
            {total === 1 ? "1 bundle" : `${total} bundles`}
          </span>
          {wishlistCount > 0 ? (
            <span className="tabler-badge tabler-badge-primary">
              {wishlistCount === 1 ? "1 con juegos de tu wishlist" : `${wishlistCount} con juegos de tu wishlist`}
            </span>
          ) : null}
          <span className="tabler-badge tabler-badge-muted">
            Página {page} de {totalPages}
          </span>
          {hasMore ? <span className="tabler-badge tabler-badge-info">Hay más páginas</span> : null}
        </div>
      </section>

      {error ? (
        <div className="space-y-3">
          <Alert variant="danger">{error}</Alert>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="secondary" onClick={() => void load(page, "replace")} loading={loading}>
              Reintentar
            </Button>
            <Button type="button" variant="ghost" onClick={() => void load(1, "replace")} disabled={loading}>
              Volver al inicio
            </Button>
          </div>
        </div>
      ) : null}

      {isInitialLoading ? <p className="app-card p-5 text-sm text-muted">Cargando...</p> : null}

      {!isInitialLoading && items.length === 0 && !error ? (
        <p className="app-card p-5 text-sm text-muted">No hay bundles activos guardados por ahora.</p>
      ) : null}

      {items.length > 0 ? (
        <div className="grid gap-3 xl:grid-cols-2">
          {items.map((item) => (
            <BundleCard key={`${item.source}:${item.bundleKey}`} item={item} />
          ))}
        </div>
      ) : null}

      {items.length > 0 ? (
        <nav className="flex flex-wrap items-center gap-2" aria-label="Paginación de bundles">
          <Button
            type="button"
            variant="secondary"
            onClick={() => void load(page - 1, "replace")}
            disabled={loading || page <= 1}
            aria-label="Página anterior"
          >
            <ChevronLeft className="h-4 w-4" aria-hidden="true" />
            Anterior
          </Button>
          <span className="tabler-badge tabler-badge-muted">
            Página {page} de {totalPages}
          </span>
          <Button
            type="button"
            variant="secondary"
            onClick={() => void load(page + 1, "replace")}
            disabled={loading || !hasMore}
            aria-label="Página siguiente"
          >
            Siguiente
            <ChevronRight className="h-4 w-4" aria-hidden="true" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            onClick={() => void load(page + 1, "append")}
            disabled={loading || !hasMore}
          >
            <ListPlus className="h-4 w-4" aria-hidden="true" />
            Cargar más
            <span className="sr-only"> (añade la siguiente página a la lista)</span>
          </Button>
        </nav>
      ) : null}
    </div>
  );
}
