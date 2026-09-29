"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Gamepad2 } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import type { SteamDiscoverItem } from "@/lib/contracts/steam";
import { formatCurrency } from "@/lib/format/currency";
import { getDiscover, type DiscoverList } from "./_lib/discover-api";

const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

function formatMinor(amountMinor: number, currency: string) {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

function formatObserved(value: string | null) {
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

function sourceLabel(item: SteamDiscoverItem) {
  if (item.usesSteamFallback) return "Steam";
  if (item.bestPricePricingType === "fx_estimate") return `${item.bestPriceLabel ?? "Tienda"} · estimado MXN`;
  return item.bestPriceLabel ?? "Tienda";
}

function DiscoverCard({ item, tone }: { readonly item: SteamDiscoverItem; readonly tone: "discount" | "low" | "none" }) {
  const hasBestPrice = item.bestCurrentPriceMinor !== null && item.bestPriceCurrency !== null;
  const bestPrice = hasBestPrice ? formatMinor(item.bestCurrentPriceMinor, item.bestPriceCurrency) : "Precio no disponible";
  const hasSteamBase = item.initialPriceMinor !== null && item.currency !== null;
  const basePrice = hasSteamBase ? formatMinor(item.initialPriceMinor, item.currency) : "—";
  const observed = formatObserved(item.observedAt);

  return (
    <Link href={`/games/${item.appId}`} className="app-card flex h-full flex-col gap-3 p-3 transition-colors hover:border-accent">
      <DiscoverThumb src={item.imageUrl} />
      <span className="min-w-0">
        <span className="block line-clamp-2 text-base font-semibold text-primary">{item.name}</span>
        <span className="mt-0.5 block text-xs text-muted">AppID {item.appId}{item.type ? ` · ${item.type}` : ""}</span>
      </span>
      <span className="flex flex-wrap items-start justify-between gap-2 border-y border-default py-2">
        <span>
          <span className="block text-xs text-muted">Mejor precio</span>
          <span className={cn("deal-price text-lg", hasBestPrice && item.bestDiscountPercent !== null && item.bestDiscountPercent > 0 ? "text-success" : "text-primary", !hasBestPrice && "text-muted")}>{bestPrice}</span>
          <span className="block text-xs text-muted">{sourceLabel(item)}</span>
        </span>
        <span className="flex flex-wrap justify-end gap-1">
          {item.bestDiscountPercent !== null && item.bestDiscountPercent > 0 ? <span className="tabler-badge tabler-badge-success">-{item.bestDiscountPercent}%</span> : null}
          {tone === "low" ? <span className="tabler-badge tabler-badge-success">En su mínimo Steam</span> : null}
        </span>
      </span>
      <span className="flex items-end justify-between gap-2">
        <span className="text-xs text-muted">Precio base Steam <span className="font-medium text-secondary">{basePrice}</span></span>
        <span className="shrink-0 text-xs font-semibold text-accent">Ver precios</span>
      </span>
      {observed ? <span className="text-xs text-muted">Steam observado {observed}</span> : null}
    </Link>
  );
}

type Section = {
  readonly list: DiscoverList;
  readonly kicker: string;
  readonly heading: string;
  readonly note: string;
  readonly empty: string;
  readonly tone: "discount" | "low" | "none";
};

const SECTIONS: readonly Section[] = [
  { list: "discount", kicker: "Mayores descuentos", heading: "Los descuentos más altos ahora mismo", note: "Ordenados por el descuento frente al precio base de Steam, usando el mejor precio comparable guardado.", empty: "Todavía no hay juegos con descuento comparable.", tone: "discount" },
  { list: "historic", kicker: "En su mínimo histórico", heading: "En su precio más bajo observado en Steam", note: "El precio actual de Steam iguala el mínimo local que tenemos registrado; el mejor precio puede pertenecer a otra tienda.", empty: "Ningún juego está en su mínimo por ahora.", tone: "low" },
  { list: "recent", kicker: "Recién observados", heading: "Últimos juegos observados", note: "Ordenados por la última observación de Steam México; las tiendas muestran su último precio guardado.", empty: "Aún no hay observaciones recientes.", tone: "none" }
];

export function DiscoverClient() {
  const [items, setItems] = useState<Record<DiscoverList, readonly SteamDiscoverItem[]>>({ discount: [], historic: [], recent: [] });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [discount, historic, recent] = await Promise.all([getDiscover("discount"), getDiscover("historic"), getDiscover("recent")]);
      setItems({ discount, historic, recent });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo cargar el descubrimiento.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (loading) return <p className="app-card p-5 text-sm text-muted">Cargando...</p>;
  if (error) return <div className="space-y-3"><Alert variant="danger">{error}</Alert><Button type="button" variant="secondary" onClick={() => void load()}>Reintentar</Button></div>;

  return (
    <div className="space-y-6" aria-live="polite">
      {SECTIONS.map((section) => (
        <section key={section.list} className="space-y-3" aria-labelledby={`discover-${section.list}`}>
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">{section.kicker}</p>
            <h2 id={`discover-${section.list}`} className="text-xl font-semibold tracking-tight text-primary">{section.heading}</h2>
            <p className="text-xs text-muted">{section.note}</p>
          </div>
          {items[section.list].length === 0 ? <div className="app-card p-8 text-center"><p className="text-sm text-muted">{section.empty}</p></div> : <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">{items[section.list].map((item) => <DiscoverCard key={item.appId} item={item} tone={section.tone} />)}</div>}
        </section>
      ))}
    </div>
  );
}
