"use client";

// Tarjeta de `/discover` (S1/S2/S3/S6). Pinta UNA oferta: la que calificó en la pestaña activa, con su
// etiqueta de tienda, su precio, su clasificación y su enlace. Nunca mezcla dos filas.
//
// Lo que esta tarjeta NO hace:
//   · no inventa un appid de Steam: si la oferta es de un juego que Steam no vende (`steamAppId` nulo), no hay
//     enlace interno `/games/...` — el único enlace es la URL externa de esa misma oferta;
//   · no inventa enlace si no hay ninguno: sin `steamAppId` y sin `dealUrl` la tarjeta no es clickeable
//     (nada de una ruta interna con un id inventado ni de un enlace a la portada de Steam);
//   · no afirma un récord histórico: un mínimo se pinta con su monto y su moneda («Mínimo del proveedor»), y
//     solo aparece cuando el backend lo reportó; un mínimo local de Steam nunca es prueba de mínimo;
//   · no dice «el mejor precio»: la tarjeta muestra la oferta que cumple la regla de la pestaña, que no tiene
//     por qué ser el precio más bajo del juego en el catálogo.
import Link from "next/link";
import { useState } from "react";
import { ExternalLink, Gamepad2 } from "lucide-react";
import { cn } from "@/lib/ui/cn";
import { formatCurrency } from "@/lib/format/currency";
import type {
  DiscoverClassification,
  DiscoverItem,
  DiscoverReason
} from "@/lib/contracts/discover";

const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

const CLASSIFICATION_BADGES: Record<DiscoverClassification, { readonly label: string; readonly tone: string }> = {
  official: { label: "Tienda oficial", tone: "tabler-badge-success" },
  authorized: { label: "Tienda autorizada", tone: "tabler-badge-success" },
  keyshop: { label: "Keyshop", tone: "tabler-badge-warning" }
};

// El motivo es un código de máquina del backend; el copy vive aquí y en ningún caso se muestra un número de
// nota sin su escala.
const REASON_LABELS: Record<DiscoverReason, string> = {
  price: "precio bajo",
  score: "buena nota",
  history_low: "mínimo verificado"
};

function formatMinor(amountMinor: number, currency: string): string {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

function formatObserved(value: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : observedFormatter.format(date);
}

function DiscoverThumb({ src }: { readonly src: string | null }) {
  const [failed, setFailed] = useState(false);
  const image = src && !failed ? src : null;

  return (
    <span className="mx-auto flex aspect-video w-2/3 items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)]">
      {image ? (
        <img
          src={image}
          alt=""
          width={320}
          height={180}
          loading="lazy"
          decoding="async"
          onError={() => setFailed(true)}
          className="h-full w-full object-cover"
        />
      ) : (
        <Gamepad2 className="h-8 w-8 text-muted" aria-hidden="true" />
      )}
    </span>
  );
}

export function DealCard({ item }: { readonly item: DiscoverItem }) {
  const observed = formatObserved(item.observedAt);
  const classification = item.classification ? CLASSIFICATION_BADGES[item.classification] : null;
  const detailsHref = item.steamAppId !== null ? `/games/${item.steamAppId}` : null;
  const reasons = item.reasons.map((reason) => REASON_LABELS[reason]).filter(Boolean);
  const historyLow = item.historyLowMinor !== null && item.historyLowCurrency !== null
    ? { minor: item.historyLowMinor, currency: item.historyLowCurrency }
    : null;

  const title = detailsHref ? (
    <Link
      href={detailsHref}
      className="block line-clamp-2 text-base font-semibold text-primary hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
    >
      {item.title}
    </Link>
  ) : (
    <span className="block line-clamp-2 text-base font-semibold text-primary">{item.title}</span>
  );

  return (
    <article className="app-card flex h-full flex-col gap-3 p-3">
      <DiscoverThumb src={item.imageUrl} />

      <div className="min-w-0 space-y-1">
        {title}
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-semibold text-secondary">{item.shopName}</span>
          <span className={cn("tabler-badge", classification?.tone ?? "tabler-badge-muted")}>
            {classification?.label ?? "Sin clasificar"}
          </span>
          {item.score !== null ? (
            <span className="text-lg font-bold tabular-nums text-primary">
              {`Nota ${item.score.toFixed(1)}/10`}
            </span>
          ) : null}
        </div>
      </div>

      <div className="space-y-1 border-y border-default py-2">
        <div className="flex flex-wrap items-baseline gap-2">
          {item.usesFxEstimate ? (
            <>
              <span className="deal-price text-2xl text-[color:var(--color-success)]">{`≈ ${formatMinor(item.mxnPriceMinor, "MXN")}`}</span>
              <span className="tabler-badge tabler-badge-warning">Estimado</span>
            </>
          ) : (
            <span className="deal-price text-2xl text-[color:var(--color-success)]">{formatMinor(item.mxnPriceMinor, "MXN")}</span>
          )}
        </div>
        {item.usesFxEstimate ? (
          <p className="text-xs text-muted">
            {item.originalPriceMinor !== null
              ? `Precio original ${formatMinor(item.originalPriceMinor, item.originalCurrency)}. `
              : ""}
            Conversión estimada con el tipo de cambio del día; no es un precio regional de México.
          </p>
        ) : (
          <p className="text-xs text-muted">Precio regional en MXN de la tienda, sin conversión.</p>
        )}
      </div>

      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
        {historyLow ? (
          <span>{`Mínimo del proveedor ${formatMinor(historyLow.minor, historyLow.currency)}`}</span>
        ) : null}
        {observed ? <span>{`Observado ${observed}`}</span> : null}
      </div>

      {reasons.length > 0 ? (
        <p className="text-xs text-muted">{`Cumple: ${reasons.join(" · ")}`}</p>
      ) : null}

      <div className="mt-auto flex flex-wrap items-center justify-between gap-2 pt-1">
        {item.dealUrl ? (
          <a
            href={item.dealUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1.5 text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            {`Ver oferta en ${item.shopName}`}
            <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
            <span className="sr-only">(se abre en una pestaña nueva)</span>
          </a>
        ) : (
          <span className="text-xs text-muted">Sin enlace de la tienda</span>
        )}
        {detailsHref ? (
          <Link
            href={detailsHref}
            className="text-xs font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            Ver ficha
          </Link>
        ) : (
          <span className="text-xs text-muted">Sin ficha en Steam</span>
        )}
      </div>
    </article>
  );
}
