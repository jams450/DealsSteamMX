"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ExternalLink, Gamepad2, Trophy } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import type { SteamBundleTier, SteamGame, SteamGameBundle, SteamGameOffer } from "@/lib/contracts/steam";
import { getSteamGame, refreshSteamGame } from "@/app/steam/_lib/steam-api";
import { storeLabel } from "@/lib/contracts/stores";
import { formatReviewMonth } from "@/lib/contracts/reviews";
import { formatCurrency } from "@/lib/format/currency";

interface GameClientProps {
  readonly appId: number;
}

// Atribución: es requisito de la ToS de ambos proveedores. Hipervínculo activo, nunca texto plano.
const ITAD_ATTRIBUTION_URL = "https://isthereanydeal.com";
const GGDEALS_ATTRIBUTION_URL = "https://gg.deals";

const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });
const rateFormatter = new Intl.NumberFormat("es-MX", { minimumFractionDigits: 2, maximumFractionDigits: 4 });

function steamStoreUrl(appId: number) {
  return `https://store.steampowered.com/app/${appId}/`;
}

function formatMinor(amountMinor: number, currency: string) {
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

// El normalizador ya descarta fechas inválidas; el guard evita que Intl.format lance si algo se cuela.
function formatObserved(value: string | null) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : observedFormatter.format(date);
}

// La fecha de tasa llega como YYYY-MM-DD; se arma a mano para no correrla por zona horaria.
function formatIsoDate(value: string | null) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? "");
  return match ? `${match[3]}/${match[2]}/${match[1]}` : null;
}

function offerPriceMinor(offer: SteamGameOffer) {
  const current = offer.originalCurrentPriceMinor;
  if (current === null) return { display: "—", free: false };
  if (current === 0) return { display: "Gratis", free: true };
  return { display: formatMinor(current, offer.originalCurrency), free: false };
}

function offerMxnCell(offer: SteamGameOffer) {
  const value = offer.mxnCurrentPriceMinor;
  const approximate = offer.pricingType === "fx_estimate";
  const display = value === null || value === undefined
    ? "—"
    : `${approximate ? "≈ " : ""}${formatMinor(value, "MXN")}`;

  const rate = offer.fxRate === null || offer.fxRate === undefined ? null : rateFormatter.format(offer.fxRate);
  const date = formatIsoDate(offer.fxRateDate);
  const parts = [rate ? `Tasa ${rate}` : null, date, offer.fxSource].filter((part): part is string => Boolean(part));

  return {
    display,
    note: approximate && parts.length > 0 ? parts.join(" · ") : null,
    unconverted: offer.pricingType === "unconverted"
  };
}

function offerKey(offer: SteamGameOffer) {
  return `${offer.source}:${offer.offerKey}`;
}

// El normalizador ya exige https; se revalida aquí porque el valor llega a un `href`.
function safeDealUrl(value: string | null) {
  if (!value) return null;
  try {
    return new URL(value).protocol === "https:" ? value : null;
  } catch {
    return null;
  }
}

// Precio publicado del tier (nativo, en `currency`). El ahorro lo aporta `tierComparisonView`, nunca
// este helper: sin comparación válida aquí no hay cifra que interpretar.
function tierPrice(tier: SteamBundleTier) {
  // El bundle solo puede venir de ITAD en V1: sin precio en el proveedor se dice tal cual, para que no
  // se lea como un fallo nuestro. La cifra nunca se sustituye por un cero ni por un "gratis".
  if (tier.priceMinor === null || tier.currency === null) return { display: "Sin precio en ITAD", muted: true };
  return { display: tier.priceMinor === 0 ? "Gratis" : formatMinor(tier.priceMinor, tier.currency), muted: false };
}

// `status`/`reason` son códigos de máquina; el copy vive en la UI. Un motivo desconocido no inventa cifras.
const BUNDLE_REASON_LABELS: Record<string, string> = {
  no_tier_price: "ITAD no publica un precio único para este bundle",
  addon: "El tier es un addon",
  items_incomplete: "Contenido no listado completo",
  item_unpriced: "Falta el precio individual de algún juego",
  not_comparable: "El tier incluye ítems no comparables",
  currency_mismatch: "Monedas distintas entre ítems",
  stale_snapshot: "Precios del bundle desactualizados"
};

function bundleReasonLabel(reason: string | null) {
  return (reason !== null ? BUNDLE_REASON_LABELS[reason] : undefined) ?? "Motivo no especificado";
}

// El MXN derivado de FX es siempre estimación, nunca precio final: se etiqueta y se muestra la tasa.
function mxnEstimate(amountMinor: number | null, tier: SteamBundleTier) {
  if (amountMinor === null || tier.pricingType !== "fx_estimate") return null;

  const rate = tier.fxRate === null ? null : `tasa ${rateFormatter.format(tier.fxRate)}`;
  const note = [rate, formatIsoDate(tier.fxRateDate), tier.fxSource].filter((part): part is string => Boolean(part)).join(" · ");
  return `≈ ${formatMinor(amountMinor, "MXN")} · FX estimado${note ? ` (${note})` : ""}`;
}

interface TierComparisonView {
  readonly bundleDisplay: string;
  readonly individualDisplay: string;
  readonly savingsText: string;
  readonly savingsPositive: boolean;
  readonly mxnIndividual: string | null;
  readonly mxnSavings: string | null;
}

/**
 * Vista de un tier con comparación válida (`status === "ok"`). El verde se decide **solo** por
 * `savingsMinor > 0`: `ok` significa que la comparación es posible, no que el bundle sea más barato
 * (el ahorro puede ser 0 o negativo). Devuelve `null` si falta algún campo, y entonces el tier cae al
 * copy por `reason` sin ninguna cifra.
 */
function tierComparisonView(tier: SteamBundleTier): TierComparisonView | null {
  const currency = tier.currency;
  const bundleMinor = tier.bundlePriceMinor ?? tier.priceMinor;
  const individualTotal = tier.individualTotalMinor;
  if (currency === null || bundleMinor === null || individualTotal === null) return null;

  const savingsMinor = tier.savingsMinor;
  let savingsText: string;
  let savingsPositive = false;
  if (savingsMinor === null) {
    savingsText = "Comparación publicada sin cifra de ahorro.";
  } else if (savingsMinor > 0) {
    savingsPositive = true;
    const percent = tier.savingsPercent !== null ? ` (${tier.savingsPercent}%)` : "";
    savingsText = `Ahorro ${formatMinor(savingsMinor, currency)}${percent} frente a comprar los juegos por separado`;
  } else if (savingsMinor === 0) {
    savingsText = "El bundle no sale más barato: mismo precio que comprar los juegos por separado.";
  } else {
    savingsText = `El bundle no sale más barato: cuesta ${formatMinor(-savingsMinor, currency)} más que comprar los juegos por separado.`;
  }

  return {
    bundleDisplay: formatMinor(bundleMinor, currency),
    individualDisplay: formatMinor(individualTotal, currency),
    savingsText,
    savingsPositive,
    mxnIndividual: mxnEstimate(individualTotal, tier),
    mxnSavings: savingsPositive ? mxnEstimate(tier.mxnSavingsMinor, tier) : null
  };
}

// Única base comparable entre tiendas: el snapshot en MXN. La moneda original nunca se compara.
const COMPARISON_CURRENCY = "MXN";

interface ComparableOffer {
  readonly offer: SteamGameOffer;
  readonly mxnMinor: number;
}

function comparableOffers(offers: readonly SteamGameOffer[]): readonly ComparableOffer[] {
  return offers.flatMap((offer) =>
    offer.mxnCurrentPriceMinor !== null && offer.pricingType !== "unconverted"
      ? [{ offer, mxnMinor: offer.mxnCurrentPriceMinor }]
      : []
  );
}

function cheapestTies(offers: readonly SteamGameOffer[]): readonly ComparableOffer[] {
  const comparable = comparableOffers(offers);
  if (comparable.length === 0) return [];
  const cheapest = Math.min(...comparable.map((candidate) => candidate.mxnMinor));
  return comparable.filter((candidate) => candidate.mxnMinor === cheapest);
}

// Claves de las filas empatadas en el precio más bajo **dentro de un grupo**: la comparación nunca
// cruza de proveedor, así que las filas se marcan por grupo.
function cheapestOfferKeys(offers: readonly SteamGameOffer[]): ReadonlySet<string> {
  return new Set(cheapestTies(offers).map((candidate) => offerKey(candidate.offer)));
}

function formatComparablePrice(mxnMinor: number) {
  return mxnMinor === 0 ? "Gratis" : formatMinor(mxnMinor, COMPARISON_CURRENCY);
}

interface HistoricalLowCandidate {
  readonly label: string;
  readonly mxnMinor: number;
  readonly approximate: boolean;
}

function historicalLowCandidate(offer: SteamGameOffer): HistoricalLowCandidate | null {
  const low = offer.historyLowAllMinor;
  const currency = offer.historyLowCurrency?.toUpperCase();
  if (low === null || currency === null) return null;

  if (currency === COMPARISON_CURRENCY) {
    return {
      label: `${offer.source === "ggdeals" ? offer.shopName : "ITAD"} · mínimo histórico`,
      mxnMinor: low,
      approximate: false
    };
  }

  // Solo FX USD→MXN cuando tasa pertenece a moneda actual de oferta; no convertir otras monedas.
  if (currency !== "USD" || offer.originalCurrency.toUpperCase() !== "USD" || offer.fxRate === null || offer.pricingType === "unconverted") {
    return null;
  }

  return {
    label: `${offer.source === "ggdeals" ? offer.shopName : "ITAD"} · mínimo histórico`,
    mxnMinor: Math.round(low * offer.fxRate),
    approximate: true
  };
}

function historicalLowCandidates(game: SteamGame, offers: readonly SteamGameOffer[]): readonly HistoricalLowCandidate[] {
  const steam = game.lowestPriceMinor === null
    ? []
    : [{ label: "Steam · observado localmente", mxnMinor: game.lowestPriceMinor, approximate: false }];

  return [
    ...steam,
    ...offers.flatMap((offer) => {
      const candidate = historicalLowCandidate(offer);
      return candidate === null ? [] : [candidate];
    })
  ];
}

interface BestPrice {
  readonly kind: "steam" | "offer";
  readonly label: string;
  readonly url: string | null;
  readonly mxnMinor: number;
  readonly approximate: boolean;
  readonly better: boolean;
  readonly note: string | null;
}

/**
 * Mejor precio comparable **de un solo grupo de proveedor**: el precio directo de Steam (solo si la
 * moneda de la ficha es MXN) contra las ofertas comparables de ese grupo. Empate: gana Steam; entre
 * tiendas, orden léxico por tienda y offerKey.
 * Una estimación por tipo de cambio nunca se marca como mejor: no se presenta como si superara a un
 * precio regional, aunque su número en MXN sea menor.
 */
function selectBestPrice(game: SteamGame, offers: readonly SteamGameOffer[]): BestPrice | null {
  const steamPrice = game.currency?.toUpperCase() === COMPARISON_CURRENCY ? game.currentPriceMinor : null;
  const cheapest = cheapestTies(offers)
    .slice()
    .sort(
      (a, b) =>
        a.offer.shopName.localeCompare(b.offer.shopName, "es-MX") ||
        a.offer.offerKey.localeCompare(b.offer.offerKey, "es-MX")
    )[0] ?? null;

  if (steamPrice === null && cheapest === null) return null;

  if (steamPrice !== null && (cheapest === null || steamPrice <= cheapest.mxnMinor)) {
    return {
      kind: "steam",
      label: "Steam · precio directo",
      url: steamStoreUrl(game.appId),
      mxnMinor: steamPrice,
      approximate: false,
      better: false,
      note: null
    };
  }

  if (cheapest === null) return null;

  const approximate = cheapest.offer.pricingType === "fx_estimate";

  return {
    kind: "offer",
    label: cheapest.offer.shopName,
    url: safeDealUrl(cheapest.offer.dealUrl),
    mxnMinor: cheapest.mxnMinor,
    approximate,
    better: !approximate && (steamPrice === null || cheapest.mxnMinor < steamPrice),
    note: offerMxnCell(cheapest.offer).note
  };
}

interface NameBadgesProps {
  readonly names: readonly string[];
  readonly legend: string;
  readonly tone: "info" | "muted";
}

// Máximo dos badges visibles por categoría; el resto queda en `+N` con el detalle en sr-only.
function NameBadges({ names, legend, tone }: NameBadgesProps) {
  if (names.length === 0) return null;

  const visible = names.slice(0, 2);
  const rest = names.slice(2);
  const badgeTone = tone === "info" ? "tabler-badge-info" : "tabler-badge-muted";

  return (
    <span className="mt-1 flex flex-wrap items-center gap-1">
      <span className="sr-only">{legend}: </span>
      {visible.map((name, index) => (
        <span key={`${name}-${index}`} className={cn("tabler-badge", badgeTone)}>{name}</span>
      ))}
      {rest.length > 0 ? (
        <span className={cn("tabler-badge", badgeTone)}>
          +{rest.length}
          <span className="sr-only">: {rest.join(", ")}</span>
        </span>
      ) : null}
    </span>
  );
}

interface AttributionLinkProps {
  readonly href: string;
  readonly label: string;
}

function AttributionLink({ href, label }: AttributionLinkProps) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex items-center gap-1.5 text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
    >
      {label}
      <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
      <span className="sr-only">(se abre en una pestaña nueva)</span>
    </a>
  );
}

interface AggregateOfferListProps {
  readonly id: string;
  readonly heading: string;
  readonly gameName: string;
  readonly offers: readonly SteamGameOffer[];
  readonly cheapest: ReadonlySet<string>;
}

/**
 * Grupo agregado (gg.deals). Cada fila es un grupo de tiendas, no una tienda: la API no devuelve
 * precio base ni porcentaje de descuento, así que una tabla con esas columnas quedaría vacía para
 * siempre. Se muestra una lista compacta (precio actual, moneda dentro del precio formateado, MXN
 * aproximado e histórico) que sigue funcionando si un proveedor futuro publica varias filas.
 */
function AggregateOfferList({ id, heading, gameName, offers, cheapest }: AggregateOfferListProps) {
  if (offers.length === 0) return null;

  return (
    <section className="space-y-2" aria-labelledby={id}>
      <h3 id={id} className="text-sm font-semibold tracking-tight text-primary">{heading}</h3>
      <p className="text-xs text-muted">
        Agregado por grupo de tiendas, no por tienda: «GG.deals» mezcla tiendas oficiales y autorizadas sin
        identificar cuál, y «GG.deals keyshops» es un agregado de keyshops sin nombre de vendedor. Por eso
        no hay columnas de precio base ni de descuento: el proveedor no las publica.
      </p>
      <p className="sr-only">Ofertas de {gameName} en {heading.toLowerCase()}</p>
      <div className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4">
        <ul className="space-y-3">
          {offers.map((offer) => {
            const price = offerPriceMinor(offer);
            const mxn = offerMxnCell(offer);
            const isCheapest = cheapest.has(offerKey(offer));
            const historyLow = offer.historyLowAllMinor !== null && offer.historyLowCurrency !== null
              ? formatMinor(offer.historyLowAllMinor, offer.historyLowCurrency)
              : null;
            const observed = formatObserved(offer.observedAt);
            return (
              <li key={offerKey(offer)} className="border-t border-default pt-3 first:border-t-0 first:pt-0">
                <div className="flex flex-wrap items-center gap-1.5">
                  {offer.dealUrl ? (
                    <a
                      href={offer.dealUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="inline-flex items-center gap-1.5 text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                    >
                      {offer.shopName}
                      <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                      <span className="sr-only">(se abre en una pestaña nueva)</span>
                    </a>
                  ) : (
                    <span className="text-sm font-semibold text-primary">{offer.shopName}</span>
                  )}
                  {offer.classification === "keyshop" ? (
                    <span className="tabler-badge tabler-badge-muted">Keyshop</span>
                  ) : null}
                  {isCheapest ? (
                    <span className="tabler-badge tabler-badge-success">
                      Más barato
                      <span className="sr-only"> entre las tiendas comparadas en MXN de este proveedor</span>
                    </span>
                  ) : null}
                </div>
                <div className="mt-1 flex flex-wrap items-baseline gap-x-3 gap-y-1">
                  <span className={cn("deal-price", price.free ? "text-success" : price.display === "—" ? "text-muted" : "text-primary")}>
                    {price.display}
                  </span>
                  {mxn.unconverted ? (
                    <span className="tabler-badge tabler-badge-warning">Sin conversión</span>
                  ) : (
                    <span className={cn("deal-price", isCheapest ? "text-success" : "text-primary")}>{mxn.display}</span>
                  )}
                  {mxn.note ? <span className="text-xs text-muted">{mxn.note}</span> : null}
                </div>
                <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1">
                  {historyLow ? (
                    <span className="tabler-badge tabler-badge-info">Mínimo histórico {historyLow}</span>
                  ) : null}
                  <span className="text-xs text-muted">
                    {observed ? `Observado ${observed}` : "Sin fecha de observación"}
                  </span>
                </div>
              </li>
            );
          })}
        </ul>
      </div>
    </section>
  );
}

interface OfferGroupProps {
  readonly id: string;
  readonly heading: string;
  readonly gameName: string;
  readonly offers: readonly SteamGameOffer[];
  readonly cheapest: ReadonlySet<string>;
}

function OfferGroup({ id, heading, gameName, offers, cheapest }: OfferGroupProps) {
  if (offers.length === 0) return null;

  return (
    <section className="space-y-2" aria-labelledby={id}>
      <h3 id={id} className="text-sm font-semibold tracking-tight text-primary">{heading}</h3>
      <div className="table-shell overflow-x-auto">
        <table className="w-full min-w-max text-left text-sm">
          <caption className="sr-only">Ofertas de {gameName} en {heading.toLowerCase()}</caption>
          <thead className="table-head">
            <tr>
              <th scope="col" className="p-3">Tienda</th>
              <th scope="col" className="p-3">Precio base</th>
              <th scope="col" className="p-3">Descuento</th>
              <th scope="col" className="p-3">Precio</th>
              <th scope="col" className="p-3">Moneda</th>
              <th scope="col" className="p-3">Aprox. MXN</th>
              <th scope="col" className="p-3">Observado</th>
              <th scope="col" className="p-3">Mínimo histórico</th>
            </tr>
          </thead>
          <tbody>
            {offers.map((offer) => {
              const price = offerPriceMinor(offer);
              const mxn = offerMxnCell(offer);
              const base = offer.originalRegularPriceMinor;
              const historyLow = offer.historyLowAllMinor !== null && offer.historyLowCurrency !== null
                ? formatMinor(offer.historyLowAllMinor, offer.historyLowCurrency)
                : null;
              const isCheapest = cheapest.has(offerKey(offer));
              return (
                <tr key={offerKey(offer)} className="table-row">
                  <td className="table-cell p-3 font-medium text-primary">
                    <span className="flex flex-wrap items-center gap-1.5">
                      {offer.dealUrl ? (
                        <a
                          href={offer.dealUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-1.5 text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                        >
                          {offer.shopName}
                          <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                          <span className="sr-only">(se abre en una pestaña nueva)</span>
                        </a>
                      ) : (
                        offer.shopName
                      )}
                      {offer.classification === "official" ? (
                        <span className="tabler-badge tabler-badge-info">Oficial</span>
                      ) : offer.classification === "keyshop" ? (
                        <span className="tabler-badge tabler-badge-muted">Keyshop</span>
                      ) : null}
                      {isCheapest ? (
                        <span className="tabler-badge tabler-badge-success">
                          Más barato
                          <span className="sr-only"> entre las tiendas comparadas en MXN de este proveedor</span>
                        </span>
                      ) : null}
                    </span>
                    <NameBadges names={offer.drmNames} legend="DRM" tone="info" />
                    <NameBadges names={offer.platformNames} legend="Plataformas" tone="muted" />
                  </td>
                  <td className={cn("table-cell deal-price p-3", offer.discountPercent ? "deal-price-strike" : "text-primary")}>
                    {base === null || base === undefined ? "—" : formatMinor(base, offer.originalCurrency)}
                  </td>
                  <td className="table-cell p-3">
                    {offer.discountPercent ? (
                      <span className="tabler-badge tabler-badge-success">-{offer.discountPercent}%</span>
                    ) : (
                      <span className="text-muted">—</span>
                    )}
                  </td>
                  <td className={cn("table-cell deal-price p-3", price.free || isCheapest ? "text-success" : price.display === "—" ? "text-muted" : "text-primary")}>
                    {price.display}
                  </td>
                  <td className="table-cell p-3 font-semibold uppercase text-primary">{offer.originalCurrency}</td>
                  <td className="table-cell p-3">
                    {mxn.unconverted ? (
                      <span className="tabler-badge tabler-badge-warning">Sin conversión</span>
                    ) : (
                      <span className={cn("deal-price", isCheapest ? "text-success" : "text-primary")}>{mxn.display}</span>
                    )}
                    {mxn.note ? <span className="block text-xs text-muted">{mxn.note}</span> : null}
                  </td>
                  <td className="table-cell p-3 text-muted">
                    {formatObserved(offer.observedAt) ?? "—"}
                  </td>
                  <td className="table-cell p-3">
                    {historyLow ? (
                      <span className="tabler-badge tabler-badge-info">
                        {historyLow} · mínimo histórico ITAD (juego)
                      </span>
                    ) : (
                      <span className="text-muted">—</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

interface BundleCardProps {
  readonly bundle: SteamGameBundle;
  readonly gameName: string;
}

/**
 * Bundle externo: bloque propio, nunca una fila de ofertas ni parte del «mejor precio». Por tier
 * muestra precio del bundle, total individual y ahorro **solo** cuando la comparación es válida
 * (`status === "ok"`); si no, muestra el motivo en texto y ninguna cifra. El verde se limita a
 * `savingsMinor > 0`.
 */
function BundleCard({ bundle, gameName }: BundleCardProps) {
  const expiresDisplay = formatObserved(bundle.expiresAt);
  const publishedDisplay = formatObserved(bundle.publishedAt);
  const observedDisplay = formatObserved(bundle.observedAt);
  const href = safeDealUrl(bundle.dealUrl ?? bundle.pageUrl);
  const hasValidComparison = bundle.tiers.some((tier) => tier.status === "ok");

  return (
    <article className="space-y-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4">
      <p className="sr-only">Bundle de {gameName}</p>
      <div className="space-y-1">
        <div className="flex flex-wrap items-center gap-1.5">
          {href ? (
            <a
              href={href}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              {bundle.title}
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              <span className="sr-only">(se abre en una pestaña nueva)</span>
            </a>
          ) : (
            <span className="text-sm font-semibold text-primary">{bundle.title}</span>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
          {bundle.shopName ? <span className="font-semibold text-secondary">{bundle.shopName}</span> : null}
          {publishedDisplay ? <span>Publicado {publishedDisplay}</span> : null}
          {expiresDisplay ? <span>Expira {expiresDisplay}</span> : null}
          {observedDisplay ? <span>Observado {observedDisplay}</span> : null}
        </div>
      </div>

      {bundle.details ? <p className="text-xs text-secondary">{bundle.details}</p> : null}

      {bundle.tiers.length > 0 ? (
        <ul className="space-y-3">
          {bundle.tiers.map((tier, tierIndex) => {
            const price = tierPrice(tier);
            const comparison = tier.status === "ok" ? tierComparisonView(tier) : null;
            return (
              <li key={tierIndex} className="border-t border-default pt-3 first:border-t-0 first:pt-0">
                {comparison ? (
                  <div className="space-y-1">
                    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                      <span className="text-xs text-muted">Precio del bundle</span>
                      <span className="deal-price text-primary">{comparison.bundleDisplay}</span>
                      {tier.currency ? (
                        <span className="text-xs font-semibold uppercase text-secondary">{tier.currency}</span>
                      ) : null}
                      {tier.addon ? <span className="tabler-badge tabler-badge-warning">Addon</span> : null}
                    </div>
                    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                      <span className="text-xs text-muted">Total individual</span>
                      <span className="deal-price text-primary">{comparison.individualDisplay}</span>
                    </div>
                    <p className={cn("text-sm font-semibold", comparison.savingsPositive ? "text-success" : "text-secondary")}>
                      {comparison.savingsText}
                    </p>
                    {comparison.mxnIndividual ? (
                      <p className="text-xs text-muted">Total individual estimado en MXN: {comparison.mxnIndividual}</p>
                    ) : null}
                    {comparison.mxnSavings ? (
                      <p className="text-xs text-muted">Ahorro estimado en MXN: {comparison.mxnSavings}</p>
                    ) : null}
                  </div>
                ) : (
                  <div className="space-y-1">
                    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                      <span className={cn("deal-price", price.muted ? "text-muted" : "text-primary")}>{price.display}</span>
                      {tier.currency ? (
                        <span className="text-xs font-semibold uppercase text-secondary">{tier.currency}</span>
                      ) : null}
                      {tier.addon ? <span className="tabler-badge tabler-badge-warning">Addon</span> : null}
                      {tier.games.length === 0 ? (
                        <span className="tabler-badge tabler-badge-muted">Contenido no detallado</span>
                      ) : null}
                    </div>
                    <p className="text-xs text-muted">Sin comparación: {bundleReasonLabel(tier.reason)}</p>
                  </div>
                )}
                {tier.games.length > 0 ? (
                  <ul className="mt-2 flex flex-wrap gap-1">
                    {tier.games.map((item, itemIndex) => {
                      const typeLabel = item.type && item.type.toLowerCase() !== "game" ? item.type : null;
                      return (
                        <li key={`${item.title}-${itemIndex}`} className="tabler-badge tabler-badge-muted">
                          {item.title}
                          {typeLabel ? <span className="ml-1 text-muted">· {typeLabel}</span> : null}
                        </li>
                      );
                    })}
                  </ul>
                ) : null}
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="tabler-badge tabler-badge-muted">Sin tiers publicados por el proveedor</p>
      )}

      {hasValidComparison ? (
        <p className="text-xs text-muted">
          La comparación usa los precios actuales de ITAD en el momento de la observación; pueden cambiar
          y la disponibilidad no está garantizada.
        </p>
      ) : null}
    </article>
  );
}

export function GameClient({ appId }: GameClientProps) {
  const [game, setGame] = useState<SteamGame | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [coverFailed, setCoverFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);

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

  // La actualización de ofertas no toca `loading`: la página no debe parpadear.
  async function refreshOffers() {
    if (refreshing) return;
    setRefreshing(true);
    setRefreshError(null);
    try {
      setGame(await refreshSteamGame(appId));
    } catch (cause) {
      // Un fallo de un proveedor nunca borra el detalle ya cargado.
      setRefreshError(cause instanceof Error ? cause.message : "No se pudieron actualizar las ofertas.");
    } finally {
      setRefreshing(false);
    }
  }

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
  const lowestDate = formatObserved(game.lowestPriceAt);
  const observedDisplay = formatObserved(game.observedAt);
  const itadRefreshedDisplay = formatObserved(game.offersRefreshedAt);
  const ggDealsRefreshedDisplay = formatObserved(game.ggDealsRefreshedAt);
  const bundlesRefreshedDisplay = formatObserved(game.bundlesRefreshedAt);

  const coverUrl = game.imageUrl && !coverFailed ? game.imageUrl : null;
  const storeUrl = steamStoreUrl(game.appId);

  // Posesión según la biblioteca: tiendas con identidad exacta, Game Pass y coincidencias candidatas.
  // Si no hay nada que mostrar, el bloque no existe (sin contenedor vacío ni espacio extra).
  const ownershipOwnedStores = game.ownership.ownedStores;
  const ownershipPossibleStores = game.ownership.possibleMatchStores;
  const hasOwnershipBadges =
    ownershipOwnedStores.length > 0 || game.ownership.hasGamePass || ownershipPossibleStores.length > 0;

  // Agrupado por proveedor: cada grupo compara y marca sus propias filas. `classification` ya no
  // filtra nada, solo decide el badge (oficial / keyshop / ninguno).
  // ITAD trae una oferta por tienda → tabla. gg.deals trae un agregado por bucket → lista compacta.
  const itadOffers = (game.offers ?? []).filter((offer) => offer.source === "itad");
  const ggDealsOffers = (game.offers ?? []).filter((offer) => offer.source === "ggdeals");
  const groups = [
    { id: "offers-itad", heading: "ITAD", offers: itadOffers, cheapest: cheapestOfferKeys(itadOffers), aggregate: false },
    { id: "offers-ggdeals", heading: "gg.deals", offers: ggDealsOffers, cheapest: cheapestOfferKeys(ggDealsOffers), aggregate: true }
  ];
  const totalOffers = itadOffers.length + ggDealsOffers.length;
  const stale = game.offersStale || game.ggDealsStale;
  // Activos primero; dentro de cada grupo se respeta el orden del proveedor.
  const sortedBundles = [...game.bundles];
  const historyCandidates = historicalLowCandidates(game, game.offers ?? []);
  const globalHistoricalLow = historyCandidates.length > 0
    ? historyCandidates.reduce((lowest, candidate) => candidate.mxnMinor < lowest.mxnMinor ? candidate : lowest)
    : null;

  // Un mismo ganador (Steam) puede salir en los dos grupos: se muestra una sola vez.
  const winnerKeys = new Set<string>();
  const bestPrices = groups.flatMap((group) => {
    const best = selectBestPrice(game, group.offers);
    if (best === null) return [];
    const key = `${best.kind}|${best.label}|${best.mxnMinor}|${best.url ?? ""}`;
    if (winnerKeys.has(key)) return [];
    winnerKeys.add(key);
    return [{ group, best }];
  });

  return (
    <div className="space-y-4">
      <Link href="/search" className="btn-secondary-semantic inline-flex h-10 items-center px-4 text-sm font-semibold">
        Volver a resultados
      </Link>

      <section className="app-card-accent space-y-5 p-5">
        <div className="grid grid-cols-1 gap-5 md:grid-cols-12">
          <div className="md:col-span-4">
            {coverUrl ? (
              <img
                src={coverUrl}
                alt={`Portada de ${game.name}`}
                width={460}
                height={215}
                decoding="async"
                onError={() => setCoverFailed(true)}
                className="aspect-[460/215] h-auto w-full rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] object-cover"
              />
            ) : (
              <div
                aria-hidden="true"
                className="flex aspect-[460/215] w-full items-center justify-center rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] text-muted"
              >
                <Gamepad2 className="h-8 w-8" />
              </div>
            )}
          </div>
          <div className="min-w-0 space-y-3 md:col-span-8">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Precio Steam · México</p>
            <h2 className="text-2xl font-semibold tracking-tight text-primary">{game.name}</h2>
            <p className="text-sm text-secondary">
              AppID {game.appId}{game.type ? ` · ${game.type}` : ""}
            </p>
            {hasOwnershipBadges ? (
              <div className="flex flex-wrap items-center gap-2">
                {ownershipOwnedStores.map((store) => (
                  <span key={store} className="tabler-badge tabler-badge-info">
                    Ya lo tienes en {storeLabel(store)}
                  </span>
                ))}
                {game.ownership.hasGamePass ? (
                  <span className="tabler-badge tabler-badge-solid tabler-badge-primary">Game Pass</span>
                ) : null}
                {ownershipPossibleStores.length > 0 ? (
                  <span className="tabler-badge tabler-badge-warning">
                    Posible coincidencia en {ownershipPossibleStores.map((store) => storeLabel(store)).join(", ")}
                  </span>
                ) : null}
              </div>
            ) : null}
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
              {observedDisplay ? (
                <span className="tabler-badge tabler-badge-info">Actualizado {observedDisplay}</span>
              ) : (
                <span className="tabler-badge tabler-badge-warning">Sin fecha de actualización</span>
              )}
            </div>
          </div>
        </div>

        <div className="space-y-3 border-t border-default pt-4">
          <h3 className="text-xs font-semibold uppercase tracking-widest text-muted">Mejor precio comparable</h3>
          {bestPrices.length > 0 ? (
            <div className="grid gap-3 md:grid-cols-2">
              {bestPrices.map(({ group, best }) => (
                <article key={`${group.id}-${best.kind}-${best.mxnMinor}`} className="app-card space-y-1 p-4">
                  <p className="text-xs font-semibold uppercase tracking-widest text-muted">{group.heading}</p>
                  <p className={cn("deal-price text-2xl", best.better ? "text-success" : "text-primary")}>
                    {best.approximate ? "≈ " : ""}
                    {formatComparablePrice(best.mxnMinor)}
                  </p>
                  {best.url ? (
                    <a
                      href={best.url}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="inline-flex items-center gap-1.5 text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                    >
                      {best.label}
                      <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                      <span className="sr-only">(se abre en una pestaña nueva)</span>
                    </a>
                  ) : (
                    <span className="text-sm font-semibold text-secondary">{best.label}</span>
                  )}
                  {best.note ? <p className="text-xs text-muted">{best.note}</p> : null}
                </article>
              ))}
            </div>
          ) : (
            <p className="text-sm text-muted">Sin precio comparable en MXN por ahora.</p>
          )}
          <p className="text-xs text-muted">
            Compara solo precios en MXN, por proveedor y por separado: el precio directo de Steam y las
            ofertas comparables de ese mismo proveedor. Una estimación por tipo de cambio no se presenta
            como mejor que un precio regional.
          </p>
        </div>

        <div className="space-y-2 border-t border-default pt-4">
          <h3 className="text-xs font-semibold uppercase tracking-widest text-muted">Referencia histórica</h3>
          {globalHistoricalLow ? (
            <>
              <p className="deal-price text-lg text-primary">
                {`Menor mínimo disponible: ${globalHistoricalLow.approximate ? "≈ " : ""}${formatComparablePrice(globalHistoricalLow.mxnMinor)}`}
              </p>
              <p className="text-sm font-semibold text-secondary">{globalHistoricalLow.label}</p>
              <p className="text-xs text-muted">
                Combina el mínimo local de Steam y mínimos de proveedores; los importes no-MXN se convierten
                de forma aproximada. Las fuentes y regiones pueden diferir; sirve como guía y no equivale a
                un precio histórico único.
              </p>
            </>
          ) : null}
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
                {observedDisplay ?? "—"}
              </td>
            </tr>
          </tbody>
        </table>
      </section>

      <section className="app-card space-y-4 p-5" aria-labelledby="offers-heading">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Ofertas por proveedor</p>
            <h2 id="offers-heading" className="text-xl font-semibold tracking-tight text-primary">
              Ofertas en otras tiendas
            </h2>
            <p className="text-xs text-muted">
              Dos proveedores con formas distintas: ITAD publica oferta por tienda; gg.deals, un precio
              agregado por grupo de tiendas.
            </p>
            <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-sm font-semibold text-secondary">
              <span>Datos de precios:</span>
              <AttributionLink href={ITAD_ATTRIBUTION_URL} label="IsThereAnyDeal" />
              <span aria-hidden="true">·</span>
              <AttributionLink href={GGDEALS_ATTRIBUTION_URL} label="GG.deals" />
            </p>
          </div>
          <div className="flex flex-col items-start gap-1 sm:items-end">
            <Button
              variant="secondary"
              className="w-full sm:w-auto"
              loading={refreshing}
              loadingText="Actualizando..."
              onClick={() => void refreshOffers()}
            >
              Actualizar ofertas
            </Button>
            <span className="text-xs text-muted" aria-live="polite">
              {refreshing ? "Consultando tiendas..." : ""}
            </span>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {totalOffers > 0 ? (
            <span className="tabler-badge tabler-badge-muted">
              {totalOffers === 1 ? "1 oferta" : `${totalOffers} ofertas`}
            </span>
          ) : null}
          {itadRefreshedDisplay ? (
            <span className="tabler-badge tabler-badge-info">ITAD actualizado {itadRefreshedDisplay}</span>
          ) : itadOffers.length > 0 ? (
            <span className="tabler-badge tabler-badge-warning">Sin fecha de actualización de ITAD</span>
          ) : null}
          {ggDealsRefreshedDisplay ? (
            <span className="tabler-badge tabler-badge-info">gg.deals actualizado {ggDealsRefreshedDisplay}</span>
          ) : ggDealsOffers.length > 0 ? (
            <span className="tabler-badge tabler-badge-warning">Sin fecha de actualización de gg.deals</span>
          ) : null}
          {stale ? (
            <span className="tabler-badge tabler-badge-warning">Datos posiblemente desactualizados</span>
          ) : null}
        </div>

        {refreshError ? <Alert variant="danger">{refreshError}</Alert> : null}

        {totalOffers === 0 ? (
          <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
            Todavía no hay ofertas de ITAD ni de gg.deals para este juego. Usa «Actualizar ofertas» para consultarlas.
          </p>
        ) : (
          <div className="space-y-5">
            {groups.map((group) =>
              group.aggregate ? (
                <AggregateOfferList
                  key={group.id}
                  id={group.id}
                  heading={group.heading}
                  gameName={game.name}
                  offers={group.offers}
                  cheapest={group.cheapest}
                />
              ) : (
                <OfferGroup
                  key={group.id}
                  id={group.id}
                  heading={group.heading}
                  gameName={game.name}
                  offers={group.offers}
                  cheapest={group.cheapest}
                />
              )
            )}
          </div>
        )}
      </section>

      {sortedBundles.length > 0 ? (
        <section className="app-card space-y-4 p-5" aria-labelledby="bundles-heading">
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Bundles por proveedor</p>
            <h2 id="bundles-heading" className="text-xl font-semibold tracking-tight text-primary">Bundles encontrados</h2>
            <p className="text-xs text-muted">
              Paquetes que incluyen este juego, según ITAD. Se muestran aparte de las ofertas: un bundle
              tiene tiers y varios ítems, y caduca. Cuando ITAD publica el precio individual de todos los
              ítems, el tier compara el bundle contra comprarlos por separado; si falta algún dato, se
              muestra el motivo y ninguna cifra de ahorro.
            </p>
            <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-sm font-semibold text-secondary">
              <span>Datos de bundles:</span>
              <AttributionLink href={ITAD_ATTRIBUTION_URL} label="IsThereAnyDeal" />
            </p>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <span className="tabler-badge tabler-badge-muted">
              {sortedBundles.length === 1 ? "1 bundle" : `${sortedBundles.length} bundles`}
            </span>
            {bundlesRefreshedDisplay ? (
              <span className="tabler-badge tabler-badge-info">Bundles actualizado {bundlesRefreshedDisplay}</span>
            ) : (
              <span className="tabler-badge tabler-badge-warning">Sin fecha de actualización de bundles</span>
            )}
            {game.bundlesStale ? (
              <span className="tabler-badge tabler-badge-warning">Bundles posiblemente desactualizados</span>
            ) : null}
          </div>

          <div className="space-y-4">
            {sortedBundles.map((bundle, bundleIndex) => (
              <BundleCard
                key={bundle.bundleKey ?? `${bundle.title}-${bundleIndex}`}
                bundle={bundle}
                gameName={game.name}
              />
            ))}
          </div>
        </section>
      ) : null}

      {game.reviews.length > 0 ? (
        <section className="app-card space-y-4 p-5" aria-labelledby="reviews-heading">
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Reseñas</p>
            <h2 id="reviews-heading" className="text-xl font-semibold tracking-tight text-primary">
              Tus reseñas de este juego
            </h2>
            <p className="text-xs text-muted">
              Una reseña por plataforma. Se crean y se editan en la biblioteca; aquí solo se leen.
            </p>
          </div>
          <ul className="space-y-3">
            {game.reviews.map((review) => {
              const started = formatReviewMonth(review.startedMonth);
              const finished = formatReviewMonth(review.finishedMonth);
              const range =
                started && finished
                  ? `De ${started} a ${finished}`
                  : started
                    ? `Desde ${started}`
                    : finished
                      ? `Hasta ${finished}`
                      : null;
              return (
                <li key={review.reviewId} className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-[10px] font-semibold uppercase tracking-widest text-muted">
                      {storeLabel(review.platform)}
                    </span>
                    {review.score !== null ? (
                      <span className="tabler-badge tabler-badge-info">
                        Nota {review.score}
                        {review.scoreLabel ? ` · ${review.scoreLabel}` : ""}
                      </span>
                    ) : (
                      <span className="tabler-badge tabler-badge-muted">Sin nota</span>
                    )}
                    {review.isGoty ? (
                      <span className="tabler-badge tabler-badge-success">
                        <Trophy className="h-3 w-3" aria-hidden="true" />
                        GOTY
                      </span>
                    ) : null}
                    {range ? <span className="text-xs text-muted">{range}</span> : null}
                  </div>
                  {review.body ? (
                    <p className="mt-1 whitespace-pre-wrap break-words text-sm text-secondary">{review.body}</p>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </section>
      ) : null}
    </div>
  );
}
