"use client";

import Link from "next/link";
import { useEffect, useState, type KeyboardEvent } from "react";
import { ExternalLink, Gamepad2, Star, Trophy } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import type { SteamBundleTier, SteamGame, SteamGameBundle, SteamGameOffer } from "@/lib/contracts/steam";
import { isUbisoftPublisher } from "./_lib/ubisoft-search";
import { getSteamGame, refreshSteamGame } from "@/app/steam/_lib/steam-api";
import { setFavorite } from "@/lib/api/favorites";
import { storeLabel } from "@/lib/contracts/stores";
import { formatReviewMonth, reviewStatusLabel } from "@/lib/contracts/reviews";
import { formatCurrency } from "@/lib/format/currency";
import { pickPricedBundleTier } from "./_lib/bundle-card";

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

  return {
    display,
    unconverted: offer.pricingType === "unconverted"
  };
}

interface FxReference {
  readonly rate: number;
  readonly date: string | null;
  readonly source: string | null;
}

/**
 * Referencias de tipo de cambio que respaldan un precio pintado, no la tasa del día por sí sola: solo
 * cuentan las ofertas con `pricingType === "fx_estimate"` que traen `fxRate`.
 *
 * Se deduplican por la tupla (tasa, fecha, fuente) conservando el orden de aparición. Normalmente hay una
 * sola, pero una ficha refrescada en dos días puede traer dos tasas distintas: inventar una tasa «general»
 * sería mentir sobre el día en que se convirtió cada precio, así que se pinta una línea por tupla.
 */
function fxReferences(offers: readonly SteamGameOffer[]): readonly FxReference[] {
  const seen = new Set<string>();
  const references: FxReference[] = [];

  for (const offer of offers) {
    if (offer.pricingType !== "fx_estimate" || offer.fxRate === null) continue;
    const key = `${offer.fxRate}|${offer.fxRateDate ?? ""}|${offer.fxSource ?? ""}`;
    if (seen.has(key)) continue;
    seen.add(key);
    references.push({ rate: offer.fxRate, date: formatIsoDate(offer.fxRateDate), source: offer.fxSource });
  }

  return references;
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

/**
 * El bundle más barato con precio que incluye este juego, ya listo para pintar. La elección del tier vive
 * en `_lib/bundle-card.ts` para poder comprobarla: aquí solo queda el formato.
 *
 * El precio del bundle **no es el precio del juego**, y por eso la tarjeta no lleva la marca de «más barato
 * que Steam»: un bundle puede costar menos que el juego suelto y traer otros juegos. Es información, no una
 * oferta del juego.
 */
function bundleSummary(bundles: readonly SteamGameBundle[]) {
  const lowest = pickPricedBundleTier(bundles, COMPARISON_CURRENCY);
  if (lowest === null) return null;

  return {
    title: lowest.bundle.title,
    href: safeDealUrl(lowest.bundle.dealUrl ?? lowest.bundle.pageUrl),
    priceDisplay: lowest.priceMinor === 0 ? "Gratis" : formatMinor(lowest.priceMinor, lowest.currency),
    currency: lowest.currency,
    bundleCount: bundles.length
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

  // La etiqueta nombra al proveedor real: antes todo lo que no era gg.deals caía en «ITAD» y las
  // ofertas directas de Epic y Microsoft heredaban un nombre ajeno.
  const providerLabel =
    offer.source === "ggdeals"
      ? offer.shopName
      : offer.source === "epic"
        ? "Epic Games Store"
        : offer.source === "microsoft"
          ? "Microsoft Store"
          : "ITAD";

  if (currency === COMPARISON_CURRENCY) {
    return {
      label: `${providerLabel} · mínimo histórico`,
      mxnMinor: low,
      approximate: false
    };
  }

  // Solo FX USD→MXN cuando tasa pertenece a moneda actual de oferta; no convertir otras monedas.
  if (currency !== "USD" || offer.originalCurrency.toUpperCase() !== "USD" || offer.fxRate === null || offer.pricingType === "unconverted") {
    return null;
  }

  return {
    label: `${providerLabel} · mínimo histórico`,
    mxnMinor: Math.round(low * offer.fxRate),
    approximate: true
  };
}

function historicalLowCandidates(game: SteamGame, offers: readonly SteamGameOffer[]): readonly HistoricalLowCandidate[] {
  // El mínimo local de Steam solo entra si la ficha está en MXN. Este candidato se pinta después como
  // cifra MXN (`formatComparablePrice`), así que una ficha en otra moneda produciría un «MX$» sobre un
  // importe ajeno — y justo al lado del badge que sí lo formatea en su moneda. Es la misma condición que
  // ya usa `steamComparablePrice`: sin precio comparable en MXN, Steam no compite ni entra en el mínimo.
  const steamIsComparable = game.currency?.toUpperCase() === COMPARISON_CURRENCY;
  const steam = game.lowestPriceMinor === null || !steamIsComparable
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

/** El precio directo de Steam, solo si la ficha está en MXN: es la referencia contra la que se compara. */
function steamComparablePrice(game: SteamGame): number | null {
  return game.currency?.toUpperCase() === COMPARISON_CURRENCY ? game.currentPriceMinor : null;
}

/**
 * Mejor precio comparable **de un solo grupo de proveedor**, y de ese grupo únicamente: la oferta comparable
 * más barata. Empate: orden léxico por tienda y offerKey, para que la misma ficha elija siempre la misma fila.
 *
 * El precio directo de Steam **no entra aquí**. Entrar era el origen de dos tarjetas mentirosas: una con el
 * nombre de la tienda y el precio y el enlace de Steam (primero cuando el grupo estaba vacío, después cuando
 * Steam ganaba la comparación). Steam ya es el titular de la página, y la marca de la tarjeta dice si esta
 * tienda le gana; cuál de las dos es más barata es exactamente lo que informa la marca, no lo que decide el
 * número que se pinta.
 */
function bestGroupOffer(offers: readonly SteamGameOffer[]): ComparableOffer | null {
  return (
    cheapestTies(offers)
      .slice()
      .sort(
        (a, b) =>
          a.offer.shopName.localeCompare(b.offer.shopName, "es-MX") ||
          a.offer.offerKey.localeCompare(b.offer.offerKey, "es-MX")
      )[0] ?? null
  );
}

// Fase 6: el vocabulario de procedencia del precio se dice en la fila. Un precio regional es el precio de
// la tienda en pesos; un estimado es una conversión, y va en tono discreto porque no es comparable de
// igual a igual. `unconverted` no lleva badge aquí: su celda de MXN ya dice «Sin conversión».
function PricingBadge({ pricingType }: { readonly pricingType: SteamGameOffer["pricingType"] }) {
  if (pricingType === "regional") {
    return <span className="tabler-badge tabler-badge-success">Precio regional MX</span>;
  }
  if (pricingType === "fx_estimate") {
    return <span className="tabler-badge tabler-badge-muted">Estimado</span>;
  }
  return null;
}

/** Una búsqueda, no la ficha del juego: Ubisoft no publica un id por título que se pueda construir. La URL
 * está medida — `/ofertas/search?lang=es_MX&q=` responde 200 sin redirección, y `/es-mx/search` responde 302
 * al home, así que no es inventable. El `™` se quita del término porque es ruido para el buscador de la
 * tienda; sin él la consulta es la que un usuario escribiría. */
const UBISOFT_SEARCH_BASE = "https://store.ubisoft.com/ofertas/search?lang=es_MX&q=";

function ubisoftSearchUrl(title: string) {
  return `${UBISOFT_SEARCH_BASE}${encodeURIComponent(title.replace(/[™®]/g, "").trim())}`;
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
                  <PricingBadge pricingType={offer.pricingType} />
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
                      <PricingBadge pricingType={offer.pricingType} />
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
  const [favoritePending, setFavoritePending] = useState(false);
  const [favoriteError, setFavoriteError] = useState<string | null>(null);
  // Pestaña activa de proveedor. Solo estado local de UI: ningún dato, fetch ni refresco cambia.
  const [activeProviderTab, setActiveProviderTab] = useState("steam");

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

  // Favorito optimista: la estrella cambia al instante y se revierte si el servidor rechaza la marca.
  // Nunca se pierde el detalle ya cargado: un fallo solo escribe el aviso.
  async function toggleFavorite() {
    if (game === null || favoritePending) return;

    const next = !game.isFavorite;
    setFavoriteError(null);
    setFavoritePending(true);
    setGame({ ...game, isFavorite: next });
    try {
      await setFavorite({ steamAppId: game.appId }, next);
    } catch (cause) {
      setGame((current) => (current === null ? current : { ...current, isFavorite: !next }));
      setFavoriteError(cause instanceof Error ? cause.message : "No se pudo actualizar el favorito.");
    } finally {
      setFavoritePending(false);
    }
  }

  if (loading) return <p className="app-card p-5 text-sm text-muted">Cargando...</p>;

  if (error) {
    return (
      <div className="space-y-3">
        <Alert variant="danger">{error}</Alert>
        <Link href="/search" className="btn-secondary-semantic inline-flex h-10 items-center px-4 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">
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
  // Tienda directa (Epic, Microsoft) trae una oferta por juego con precio regional → tabla, primero porque su
  // importe es el real de la región y no una estimación.
  // ITAD trae una oferta por tienda → tabla. gg.deals trae un agregado por bucket → lista compacta.
  const epicOffers = (game.offers ?? []).filter((offer) => offer.source === "epic");
  const microsoftOffers = (game.offers ?? []).filter((offer) => offer.source === "microsoft");
  const itadOffers = (game.offers ?? []).filter((offer) => offer.source === "itad");
  const ggDealsOffers = (game.offers ?? []).filter((offer) => offer.source === "ggdeals");
  const groups = [
    { id: "offers-epic", heading: "Epic Games Store", offers: epicOffers, cheapest: cheapestOfferKeys(epicOffers), aggregate: false },
    { id: "offers-microsoft", heading: "Microsoft Store", offers: microsoftOffers, cheapest: cheapestOfferKeys(microsoftOffers), aggregate: false },
    { id: "offers-itad", heading: "ITAD", offers: itadOffers, cheapest: cheapestOfferKeys(itadOffers), aggregate: false },
    { id: "offers-ggdeals", heading: "gg.deals", offers: ggDealsOffers, cheapest: cheapestOfferKeys(ggDealsOffers), aggregate: true }
  ];
  const totalOffers = epicOffers.length + microsoftOffers.length + itadOffers.length + ggDealsOffers.length;
  const stale = game.offersStale || game.ggDealsStale;
  // Orden del proveedor, sin reordenar: el nombre anterior («Activos primero») prometía un
  // orden que este spread nunca aplicó.
  const sortedBundles = [...game.bundles];
  const historyCandidates = historicalLowCandidates(game, game.offers ?? []);
  const globalHistoricalLow = historyCandidates.length > 0
    ? historyCandidates.reduce((lowest, candidate) => candidate.mxnMinor < lowest.mxnMinor ? candidate : lowest)
    : null;

  // Referencia general de tipo de cambio, no una nota por fila: la tasa pertenece a la página entera.
  const fxRefs = fxReferences(game.offers ?? []);

  // Una tarjeta por proveedor **con** ofertas comparables, con el precio de ese proveedor. El número de
  // tarjetas refleja entonces qué tiendas tienen dato, y no quién gana cada comparación: con el resumen
  // anterior, un grupo donde Steam ganaba dejaba fuera el precio de su tienda y el conteo cambiaba de un
  // juego a otro sin que el lector pudiera saber por qué.
  const steamPrice = steamComparablePrice(game);
  const candidates = groups.flatMap((group) => {
    const best = bestGroupOffer(group.offers);
    if (best === null) return [];
    return [{
      group,
      best,
      approximate: best.offer.pricingType === "fx_estimate",
      mxnMinor: best.mxnMinor
    }];
  });
  // El verde es el precio más bajo **de todas las tarjetas**, y la etiqueta dice contra qué gana.
  //
  // Las estimaciones compiten por él. Excluirlas parecía más prudente y produjo un error peor: en Floppy
  // Knights el verde quedó en Epic a 71.99 mientras gg.deals mostraba 16.56 e ITAD 20.69 — los dos estimados,
  // y los dos más bajos. Un verde sobre una tarjeta más cara que otra de la misma pantalla se lee como que
  // el resumen no sabe sumar. La imprecisión de una conversión ya viaja en la `≈` del propio precio y el tipo
  // de tienda en el badge de la fila; el puesto del más barato es un hecho del número que se pinta.
  //
  // Steam no compite por el verde: su precio es el titular de la ficha y el árbitro de la etiqueta.
  const lowestMxn = candidates.length > 0 ? Math.min(...candidates.map((card) => card.mxnMinor)) : null;
  const bestPrices = candidates.map((card) => ({
    ...card,
    cheapest: lowestMxn !== null && card.mxnMinor === lowestMxn,
    // Sin precio de Steam en MXN no se puede afirmar que le gane a Steam; la etiqueta lo dice sin mentir.
    beatsSteam: steamPrice !== null && card.mxnMinor < steamPrice
  }));
  const bundleCard = bundleSummary(game.bundles);

  interface ProviderTab {
    readonly id: string;
    readonly label: string;
    readonly count: number | null;
  }

  // Pestañas de proveedor: una por grupo CON datos (Steam oficial siempre primero, luego las
  // directas, ITAD, gg.deals y bundles solo si tienen filas). La etiqueta es el nombre del proveedor
  // en español y el badge cuenta las filas que ese grupo ya contaba hoy; Steam no lleva cuenta porque
  // su tabla es de una sola fila.
  const providerTabs: readonly ProviderTab[] = [
    { id: "steam", label: "Steam oficial", count: null },
    ...groups.flatMap((group): readonly ProviderTab[] =>
      group.offers.length > 0 ? [{ id: group.id, label: group.heading, count: group.offers.length }] : []
    ),
    ...(sortedBundles.length > 0
      ? [{ id: "bundles", label: "Bundles", count: sortedBundles.length } as ProviderTab]
      : [])
  ];
  // Si la pestaña activa se queda sin datos tras un refresco, se vuelve a la primera con datos.
  const activeTab = providerTabs.some((tab) => tab.id === activeProviderTab)
    ? activeProviderTab
    : providerTabs[0].id;

  // Flechas/Home/End mueven el foco entre pestañas (roving tabindex): la pestaña activa es la única
  // alcanzable con Tab, como piden las pestañas accesibles.
  function onProviderTabKeyDown(event: KeyboardEvent) {
    const index = providerTabs.findIndex((tab) => tab.id === activeTab);
    let next = -1;
    if (event.key === "ArrowRight") next = (index + 1) % providerTabs.length;
    else if (event.key === "ArrowLeft") next = (index - 1 + providerTabs.length) % providerTabs.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = providerTabs.length - 1;
    else return;
    event.preventDefault();
    const tab = providerTabs[next];
    setActiveProviderTab(tab.id);
    document.getElementById(`provider-tab-${tab.id}`)?.focus();
  }

  return (
    <div className="space-y-4">
      <Link href="/search" className="btn-secondary-semantic inline-flex h-10 items-center px-4 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">
        Volver a resultados
      </Link>

      {fxRefs.length > 0 ? (
        <section className="app-card space-y-2 p-4" aria-labelledby="fx-reference-heading">
          <h2 id="fx-reference-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
            Tipo de cambio de referencia
          </h2>
          <ul className="space-y-1">
            {fxRefs.map((reference) => {
              // «USD → MXN» se escribe literal a propósito: es el único par que este producto convierte.
              // Cualquier otra moneda llega como `unconverted` y no tiene tasa que mostrar (docs/PLAN_ITAD.md).
              const parts = [
                "USD → MXN",
                `Tasa ${rateFormatter.format(reference.rate)}`,
                reference.date,
                reference.source
              ].filter((part): part is string => Boolean(part));
              return (
                <li
                  key={`${reference.rate}|${reference.date ?? ""}|${reference.source ?? ""}`}
                  className="text-sm text-secondary"
                >
                  {parts.join(" · ")}
                </li>
              );
            })}
          </ul>
          <p className="text-xs text-muted">
            Los precios marcados con ≈ son conversiones de USD a MXN con las tasas de arriba y no son el precio
            regional de la tienda.
          </p>
        </section>
      ) : null}

      <section className="app-card-accent overflow-hidden">
        {/* Portada lateral: columna acotada a la izquierda en `lg` (`lg:col-span-4` frente a
            `lg:col-span-8` del contenido), apilada encima debajo de `lg`. El marco conserva el aspecto
            16:7 con `object-cover`, así que dentro no hay hueco muerto. */}
        <div className="grid gap-5 p-5 lg:grid-cols-12">
          <div className="lg:col-span-4">
            <div className="overflow-hidden rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)]">
              {coverUrl ? (
                <img
                  src={coverUrl}
                  alt={`Portada de ${game.name}`}
                  width={480}
                  height={210}
                  decoding="async"
                  onError={() => setCoverFailed(true)}
                  className="aspect-[16/7] h-auto w-full object-cover"
                />
              ) : (
                <div
                  aria-hidden="true"
                  className="flex aspect-[16/7] w-full items-center justify-center bg-[var(--color-surface-3)] text-muted"
                >
                  <Gamepad2 className="h-8 w-8" />
                </div>
              )}
            </div>
          </div>
          <div className="min-w-0 space-y-5 lg:col-span-8">
          <div className="min-w-0 space-y-3">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Precio Steam · México</p>
            <h2 className="text-2xl font-semibold tracking-tight text-primary">{game.name}</h2>
            <div className="flex flex-wrap items-center gap-3">
              <p className="text-sm text-secondary">
                AppID {game.appId}{game.type ? ` · ${game.type}` : ""}
              </p>
              <Button
                type="button"
                variant={game.isFavorite ? "primary" : "secondary"}
                className="h-9 whitespace-nowrap px-3 text-xs"
                loading={favoritePending}
                aria-pressed={game.isFavorite}
                onClick={() => void toggleFavorite()}
              >
                <Star className={cn("h-3.5 w-3.5", game.isFavorite && "fill-current")} aria-hidden="true" />
                {game.isFavorite ? "Favorito" : "Marcar favorito"}
              </Button>
            </div>
            {favoriteError ? <Alert variant="danger">{favoriteError}</Alert> : null}
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
            {/* Panel de precio: lo que se paga hoy a la izquierda, el contexto histórico a la
                derecha. En `sm` respiran en dos columnas separadas por el filo; en móvil se apilan y
                el filo pasa a horizontal. */}
            <div className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4">
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="space-y-2">
                  {/* Precio oficial de Steam: el titular de la ficha y el árbitro de la comparación. */}
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
                    {incomplete ? <span className="tabler-badge tabler-badge-warning">Datos incompletos</span> : null}
                  </div>
                </div>
                <div className="space-y-2 border-t border-default pt-4 sm:border-l sm:border-t-0 sm:pl-4 sm:pt-0">
                  {/* Mínimo histórico de las fuentes: mínimo de todos los proveedores (y el local de Steam), un dato
                      heterogéneo de fuentes y regiones distintas. Nunca va en verde: en esta ficha el verde
                      significa «el más barato ahora mismo», y esta cifra no lo es. */}
                  {globalHistoricalLow ? (
                    <div className="space-y-1">
                      <p className="text-xs font-semibold uppercase tracking-widest text-muted">Mínimo histórico de las fuentes</p>
                      <p className="deal-price text-xl text-primary">
                        {`${globalHistoricalLow.approximate ? "≈ " : ""}${formatComparablePrice(globalHistoricalLow.mxnMinor)}`}
                      </p>
                      <p className="text-sm font-semibold text-secondary">{globalHistoricalLow.label}</p>
                    </div>
                  ) : null}

                  {/* El mínimo local de Steam es un hecho distinto del mínimo histórico de las fuentes: uno es lo que
                      nuestra base observó para Steam, el otro es el mínimo de todos los proveedores. Van separados a
                      propósito: dos cifras distintas bajo una misma etiqueta es el error que ya documentó esta ficha. */}
                  <div className="flex flex-wrap items-center gap-2">
                    {lowestDisplay ? (
                      <span className={cn("tabler-badge", atLowest ? "tabler-badge-success" : "tabler-badge-info")}>
                        Mínimo observado localmente {lowestDisplay}{lowestDate ? ` · ${lowestDate}` : ""}
                      </span>
                    ) : (
                      <span className="tabler-badge tabler-badge-muted">Sin mínimo observado localmente</span>
                    )}
                  </div>
                </div>
              </div>
            </div>

          </div>

          {/* Fecha de actualización, el único «Actualizar ofertas» de la página y los accesos a las fichas de
              tienda, en la misma fila flexible a todo lo ancho de la columna de contenido:
              «Ver en Steam» es el enlace canónico a la ficha; «Buscar en Ubisoft Store» solo aparece cuando
              Steam lista Ubisoft como publisher. Es una búsqueda por título, no una ficha ni un precio. */}
          <div className="flex flex-wrap items-center gap-3">
            {observedDisplay ? (
              <span className="tabler-badge tabler-badge-info">Actualizado {observedDisplay}</span>
            ) : (
              <span className="tabler-badge tabler-badge-warning">Sin fecha de actualización</span>
            )}
            <Button
              type="button"
              variant="secondary"
              loading={refreshing}
              loadingText="Actualizando..."
              onClick={() => void refreshOffers()}
            >
              Actualizar ofertas
            </Button>
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
            {isUbisoftPublisher(game.publishers) ? <a
              href={ubisoftSearchUrl(game.name)}
              target="_blank"
              rel="noopener noreferrer"
              className="btn-secondary-semantic inline-flex h-10 items-center gap-2 px-4 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              Buscar en Ubisoft Store
              <ExternalLink className="h-4 w-4" aria-hidden="true" />
              <span className="sr-only">(se abre en una pestaña nueva)</span>
            </a> : null}
            <span className="text-xs text-muted" aria-live="polite">
              {refreshing ? "Consultando tiendas..." : ""}
            </span>
          </div>
          {refreshError ? <Alert variant="danger">{refreshError}</Alert> : null}

          </div>
        </div>
      </section>

      <section className="space-y-3" aria-labelledby="comparable-heading">
        <h2 id="comparable-heading" className="text-xl font-semibold tracking-tight text-primary">Mejor precio comparable</h2>
        {bestPrices.length > 0 || bundleCard !== null ? (
              <div className="grid gap-3 md:grid-cols-2">
                {bestPrices.map(({ group, best, approximate, cheapest, beatsSteam }) => {
                  const dealUrl = safeDealUrl(best.offer.dealUrl);
                  const observed = formatObserved(best.offer.observedAt);
                  return (
                    // La ganadora también se lee por el borde: el token de éxito, nunca un color nuevo. El
                    // texto ya iba en verde; el borde lo hace visible sin leer la cifra.
                    <article key={`${group.id}-${best.offer.offerKey}-${best.mxnMinor}`} className={cn("app-card space-y-1 p-4", cheapest && "border-[color:var(--color-success)]")}>
                      <p className="text-xs font-semibold uppercase tracking-widest text-muted">{group.heading}</p>
                      <p className={cn("deal-price text-2xl", cheapest ? "text-success" : "text-primary")}>
                        {approximate ? "≈ " : ""}
                        {formatComparablePrice(best.mxnMinor)}
                      </p>
                      {cheapest ? (
                        // En su propio bloque: la etiqueta es un `span` en línea y sin esto quedaba pegada al
                        // enlace, que es el siguiente hermano.
                        <div>
                          <span className="tabler-badge tabler-badge-success">
                            {beatsSteam ? "Más barato que Steam" : "El más barato de las tiendas"}
                          </span>
                        </div>
                      ) : null}
                      {dealUrl ? (
                        <a
                          href={dealUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-1.5 text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                        >
                          {best.offer.shopName}
                          <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                          <span className="sr-only">(se abre en una pestaña nueva)</span>
                        </a>
                      ) : (
                        <span className="text-sm font-semibold text-secondary">{best.offer.shopName}</span>
                      )}
                      <p className="text-xs text-muted">
                        {observed ? `Observado ${observed}` : "Sin fecha de observación"}
                      </p>
                    </article>
                  );
                })}
                {bundleCard ? (
                  <article className="app-card space-y-1 p-4">
                    <p className="text-xs font-semibold uppercase tracking-widest text-muted">Bundle</p>
                    <p className="deal-price text-2xl text-primary">{bundleCard.priceDisplay}</p>
                    <span className="tabler-badge tabler-badge-info">Incluye este juego</span>
                    {bundleCard.href ? (
                      <a
                        href={bundleCard.href}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center gap-1.5 text-sm font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                      >
                        {bundleCard.title}
                        <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                        <span className="sr-only">(se abre en una pestaña nueva)</span>
                      </a>
                    ) : (
                      <span className="text-sm font-semibold text-secondary">{bundleCard.title}</span>
                    )}
                    {bundleCard.currency.toUpperCase() !== COMPARISON_CURRENCY ? (
                      <p className="text-xs text-muted">{`Precio en ${bundleCard.currency}, sin convertir.`}</p>
                    ) : null}
                    {bundleCard.bundleCount > 1 ? (
                      <p className="text-xs text-muted">
                        {`${bundleCard.bundleCount} bundles incluyen este juego: están abajo, en «Bundles encontrados».`}
                      </p>
                    ) : null}
                  </article>
                ) : null}
              </div>
            ) : (
              <p className="text-sm text-muted">Sin precio comparable en MXN por ahora.</p>
            )}
        <p className="text-xs text-muted">
          Cada tarjeta es el mejor precio en MXN de ese proveedor, y en verde queda el más bajo de todos:
          la etiqueta dice si le gana al precio directo de Steam o si es el más barato sin llegar a
          ganarle. El `≈` de un precio convertido y el tipo de tienda de la fila ya dicen de dónde sale
          cada cifra. La tarjeta de bundle dice que este juego viene dentro de uno y cuánto cuesta el
          bundle: ese importe no es el precio del juego y no entra en la comparación.
        </p>
      </section>

      {/* La tabla de Steam vive ahora en la pestaña «Steam oficial» de la sección de proveedores. */}

      <section className="app-card space-y-4 p-5" aria-labelledby="offers-heading">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Detalles</p>
          <h2 id="offers-heading" className="text-xl font-semibold tracking-tight text-primary">
            Ofertas y bundles por proveedor
          </h2>
          <p className="text-xs text-muted">
            Cada pestaña muestra un proveedor: Steam oficial, tiendas directas (Epic, Microsoft), ITAD
            por tienda y gg.deals como agregado por grupo de tiendas. Los bundles van en su pestaña y no
            entran en la comparación.
          </p>
          <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-sm font-semibold text-secondary">
            <span>Datos de precios:</span>
            <AttributionLink href={ITAD_ATTRIBUTION_URL} label="IsThereAnyDeal" />
            <span aria-hidden="true">·</span>
            <AttributionLink href={GGDEALS_ATTRIBUTION_URL} label="GG.deals" />
          </p>
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

        {totalOffers === 0 ? (
          <p className="rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4 text-sm text-muted">
            Todavía no hay ofertas de las tiendas consultadas para este juego. Usa «Actualizar ofertas» para consultarlas.
          </p>
        ) : null}

        {/* Pestañas accesibles: una por proveedor con datos. Cada panel pinta la tabla/lista que antes
            vivía apilada, sin reescribirla: la tabla de Steam, `OfferGroup`, `AggregateOfferList` y las
            `BundleCard` se mudan tal cual, con sus notas. */}
        <div role="tablist" aria-label="Proveedores" onKeyDown={onProviderTabKeyDown} className="flex flex-wrap gap-2">
          {providerTabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              role="tab"
              id={`provider-tab-${tab.id}`}
              aria-selected={tab.id === activeTab}
              aria-controls={`provider-panel-${tab.id}`}
              tabIndex={tab.id === activeTab ? 0 : -1}
              onClick={() => setActiveProviderTab(tab.id)}
              className={cn(
                "inline-flex h-9 items-center gap-1.5 rounded-[var(--radius-sm)] border px-3 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                tab.id === activeTab
                  ? "border-accent bg-[var(--color-accent-soft)] text-primary"
                  : "border-default text-secondary hover:text-primary"
              )}
            >
              {tab.label}
              {tab.count !== null ? (
                <span className="tabler-badge tabler-badge-muted">{tab.count}</span>
              ) : null}
            </button>
          ))}
        </div>

        <div
          role="tabpanel"
          id="provider-panel-steam"
          aria-labelledby="provider-tab-steam"
          tabIndex={0}
          hidden={activeTab !== "steam"}
        >
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Precio oficial</p>
            <h3 id="steam-source-heading" className="text-xl font-semibold tracking-tight text-primary">
              Precio en Steam
            </h3>
          </div>
          <div className="table-shell mt-3 overflow-x-auto">
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
          </div>
        </div>

        {groups.map((group) =>
          group.offers.length === 0 ? null : (
            <div
              key={group.id}
              role="tabpanel"
              id={`provider-panel-${group.id}`}
              aria-labelledby={`provider-tab-${group.id}`}
              tabIndex={0}
              hidden={activeTab !== group.id}
            >
              {group.id === "offers-itad" ? (
                <p className="mb-3 text-xs text-muted">
                  GOG ya cobra en MXN en su tienda, pero su importe aquí llega vía ITAD en USD convertido a
                  MXN (≈) y puede diferir del precio final en caja.
                </p>
              ) : null}
              {group.aggregate ? (
                <AggregateOfferList
                  id={group.id}
                  heading={group.heading}
                  gameName={game.name}
                  offers={group.offers}
                  cheapest={group.cheapest}
                />
              ) : (
                <OfferGroup
                  id={group.id}
                  heading={group.heading}
                  gameName={game.name}
                  offers={group.offers}
                  cheapest={group.cheapest}
                />
              )}
            </div>
          )
        )}

        {sortedBundles.length > 0 ? (
          <div
            role="tabpanel"
            id="provider-panel-bundles"
            aria-labelledby="provider-tab-bundles"
            tabIndex={0}
            hidden={activeTab !== "bundles"}
          >
            <div className="space-y-1">
              <p className="text-xs font-semibold uppercase tracking-widest text-muted">Bundles por proveedor</p>
              <h3 className="text-xl font-semibold tracking-tight text-primary">Bundles encontrados</h3>
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

            <div className="mt-3 flex flex-wrap items-center gap-2">
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

            <div className="mt-3 space-y-4">
              {sortedBundles.map((bundle, bundleIndex) => (
                <BundleCard
                  key={bundle.bundleKey ?? `${bundle.title}-${bundleIndex}`}
                  bundle={bundle}
                  gameName={game.name}
                />
              ))}
            </div>
          </div>
        ) : null}
      </section>

      {game.reviews.length > 0 ? (
        <section className="app-card space-y-4 p-5" aria-labelledby="reviews-heading">
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">Reseñas</p>
            <h2 id="reviews-heading" className="text-xl font-semibold tracking-tight text-primary">
              Tus reseñas de este juego
            </h2>
            <p className="text-xs text-muted">
              De la más reciente a la más antigua. Se crean y se editan en la biblioteca; aquí solo se leen.
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
                    <span className="tabler-badge tabler-badge-neutral">{reviewStatusLabel(review.status)}</span>
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
