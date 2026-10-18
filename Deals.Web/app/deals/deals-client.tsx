"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, ExternalLink } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { formatCurrency } from "@/lib/format/currency";
import {
  GLOBAL_DEALS_PAGE_SIZE_DEFAULT,
  type GlobalDealItem
} from "@/lib/contracts/global-deals";
import { getGlobalDeals } from "./_lib/deals-api";

const ITAD_ATTRIBUTION_URL = "https://isthereanydeal.com";
const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

function formatMinor(amountMinor: number, currency: string) {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

function formatDay(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : dateFormatter.format(date);
}

// `goodDealReason` es un código de máquina; el copy vive aquí. Nunca se muestra una nota numérica.
const REASON_LABELS = {
  discount: "Descuento",
  history_low: "Mínimo histórico verificado"
} as const;

function PriceBlock({ item }: { readonly item: GlobalDealItem }) {
  const original = formatMinor(item.currentPriceMinor, item.currency);

  if (item.pricingType === "fx_estimate" && item.mxnCurrentPriceMinor !== null) {
    return (
      <div className="space-y-1">
        <div className="flex flex-wrap items-baseline gap-2">
          <span className="deal-price text-lg text-primary">
            ≈ {formatMinor(item.mxnCurrentPriceMinor, "MXN")}
          </span>
          <span className="tabler-badge tabler-badge-warning">Estimado</span>
        </div>
        <p className="text-xs text-muted">
          Precio original {original} · conversión estimada con tipo de cambio{" "}
          {item.fxRate ?? "—"} del {item.fxRateDate ?? "—"}
          {item.fxSource ? ` (${item.fxSource})` : ""}. No es un precio regional de México.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <div className="flex flex-wrap items-baseline gap-2">
        <span className="deal-price text-lg text-primary">{original}</span>
        <span className="text-xs font-semibold uppercase text-secondary">{item.currency}</span>
        {item.pricingType === "unconverted" ? (
          <span className="tabler-badge tabler-badge-muted">Sin conversión a MXN</span>
        ) : null}
      </div>
      <p className="text-xs text-muted">Precio original en {item.currency}; la moneda es la del proveedor.</p>
    </div>
  );
}

function DealCard({ item }: { readonly item: GlobalDealItem }) {
  const expires = formatDay(item.expiresAt);
  const reason = item.goodDealReason ? REASON_LABELS[item.goodDealReason] : null;

  return (
    <article className="app-card space-y-3 p-4">
      <div className="space-y-1">
        <div className="flex flex-wrap items-center gap-1.5">
          {item.dealUrl ? (
            <a
              href={item.dealUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 text-base font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              {item.gameTitle}
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              <span className="sr-only">(se abre en una pestaña nueva)</span>
            </a>
          ) : (
            <span className="text-base font-semibold text-primary">{item.gameTitle}</span>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-semibold text-secondary">{item.shopName}</span>
          {item.discountPercent !== null ? (
            <span className="tabler-badge tabler-badge-primary">−{item.discountPercent}%</span>
          ) : null}
          {reason ? <span className="tabler-badge tabler-badge-info">{reason}</span> : null}
          {item.gameMature ? <span className="tabler-badge tabler-badge-warning">Contenido maduro</span> : null}
        </div>
      </div>

      <PriceBlock item={item} />

      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
        {item.historyLowMinor !== null && item.historyLowCurrency ? (
          <span>
            Mínimo reportado {formatMinor(item.historyLowMinor, item.historyLowCurrency)}
          </span>
        ) : null}
        {expires ? <span>Expira {expires}</span> : null}
      </div>
    </article>
  );
}

export function DealsClient() {
  const [items, setItems] = useState<readonly GlobalDealItem[]>([]);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(GLOBAL_DEALS_PAGE_SIZE_DEFAULT);
  const [total, setTotal] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);

  const load = useCallback(
    async (targetPage: number) => {
      const request = ++requestId.current;
      setLoading(true);
      setError(null);
      try {
        const result = await getGlobalDeals(targetPage, pageSize);
        if (request !== requestId.current) return;
        setPage(result.page);
        setPageSize(result.pageSize);
        setTotal(result.total);
        setHasMore(result.hasMore);
        setItems(result.items);
      } catch (cause) {
        if (request !== requestId.current) return;
        // Un error no borra lo que ya está en pantalla: la página actual se conserva y se ofrece reintentar.
        setError(cause instanceof Error ? cause.message : "No se pudieron cargar las ofertas.");
      } finally {
        if (request === requestId.current) setLoading(false);
      }
    },
    [pageSize]
  );

  useEffect(() => {
    void load(1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const isInitialLoading = loading && items.length === 0 && error === null;

  return (
    <div className="space-y-4">
      <section className="app-card space-y-1 p-5" aria-labelledby="deals-heading">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">Descubrimiento</p>
        <h2 id="deals-heading" className="text-xl font-semibold tracking-tight text-primary">
          Buenas ofertas para juegos fuera de tu wishlist
        </h2>
        <p className="text-xs text-muted">
          Ofertas individuales que ITAD reportó y que ya están guardadas; abrir esta página no consulta al
          proveedor. Solo se muestran tiendas oficiales y juegos que no pudimos identificar en tu wishlist
          por su identidad ITAD. Si un juego de tu wishlist todavía no tiene identidad ITAD, no se puede
          excluir y puede aparecer aquí: es una coincidencia desconocida, no una prueba de que no esté en
          tu wishlist. El precio original siempre aparece con su moneda; una conversión se etiqueta como
          estimada y no equivale a un precio regional de México. No hay nota ni rating: el motivo se copia
          de los campos que el proveedor publicó.
        </p>
        <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-sm font-semibold text-secondary">
          <span>Datos de precios:</span>
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
            {total === 1 ? "1 oferta" : `${total} ofertas`}
          </span>
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
            <Button type="button" variant="secondary" onClick={() => void load(page)} loading={loading}>
              Reintentar
            </Button>
            <Button type="button" variant="ghost" onClick={() => void load(1)} disabled={loading}>
              Volver al inicio
            </Button>
          </div>
        </div>
      ) : null}

      {isInitialLoading ? <p className="app-card p-5 text-sm text-muted">Cargando...</p> : null}

      {!isInitialLoading && items.length === 0 && !error ? (
        <p className="app-card p-5 text-sm text-muted">
          No hay ofertas buenas para juegos fuera de tu wishlist por ahora. El catálogo se llena desde el
          barrido de fondo; si acaba de activarse, vuelve más tarde.
        </p>
      ) : null}

      {items.length > 0 ? (
        <div className="grid gap-3 xl:grid-cols-2">
          {items.map((item) => (
            <DealCard key={item.dealKey} item={item} />
          ))}
        </div>
      ) : null}

      {items.length > 0 ? (
        <nav className="flex flex-wrap items-center gap-2" aria-label="Paginación de ofertas">
          <Button
            type="button"
            variant="secondary"
            onClick={() => void load(page - 1)}
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
            onClick={() => void load(page + 1)}
            disabled={loading || !hasMore}
            aria-label="Página siguiente"
          >
            Siguiente
            <ChevronRight className="h-4 w-4" aria-hidden="true" />
          </Button>
        </nav>
      ) : null}
    </div>
  );
}
