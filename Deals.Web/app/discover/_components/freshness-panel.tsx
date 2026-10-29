"use client";

// Panel «Estado de actualización de precios» de la LISTA DE DESEOS del usuario dentro de `/wishlist`
// (spec S4/S7/S9, tarea T3).
//
// Es una sección de LECTURA: lee `GET /api/bff/discover/freshness` (snapshots persistidos) y no llama a
// ninguna tienda, no inicia ningún job y no ofrece ninguna escritura. El lanzamiento manual del job
// `catalog.prices-refresh` vive en `/jobs`; aquí solo hay un enlace.
//
// Alcance: SOLO los juegos de la lista de deseos del usuario autenticado. El backend deriva el usuario del
// token, así que esta sección no pide ni envía un id; no cubre la biblioteca, ni los favoritos, ni los datos
// de otros usuarios.
//
// Qué pinta, y qué no (corrección S9):
//   · cuenta cada juego de la lista UNA vez en cuatro cubetas de cobertura de precios (`all`, `recent`,
//     `stale`, `missing`); los cuatro contadores llegan en el mismo sobre que la página, así que cambiar de
//     filtro no dispara cuatro pedidos;
//   · clasifica por el precio comparable MÁS BARATO GUARDADO, no por el precio más reciente entre todas las
//     tiendas: un Steam caro y reciente no puede tapar un precio externo antiguo y barato. La tarjeta pinta
//     ese mismo ganador —monto, tienda, tipo y fecha— porque los cuatro campos vienen de la misma fila;
//   · las fechas por proveedor (precio observado y última consulta) quedan en un colapsable nativo por tarjeta:
//     son el detalle, no la vista principal, y un juego puede estar al día en una tienda y atrasado en otra;
//   · NO pinta precios como ofertas: esta sección no es una tarjeta de deal, así que un precio antiguo nunca
//     se presenta como precio actual. El copy dice en texto —no solo con color— que «antiguo» no es «erróneo»
//     y que «sin precio comparable» no prueba que la consulta falló.
//
// El pedido en vuelo se cancela con `AbortController` y se numera con una generación: un cambio rápido de
// filtro o de página no puede dejar ganar a la respuesta vieja, y un error conserva en pantalla lo último
// válido (incluidos los contadores de los cuatro filtros).
import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, Gamepad2, RotateCw } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import {
  CATALOG_FRESHNESS_FILTERS,
  CATALOG_FRESHNESS_HOURS,
  CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT,
  chosenOfferStoreLabel,
  formatChosenOfferPrice,
  freshnessCountFromCounts,
  freshnessShortStatusLabel,
  freshnessSourceLabel,
  hasUnresolvedWishlistEntries,
  unresolvedWishlistEntriesCount,
  type CatalogFreshnessCounts,
  type CatalogFreshnessFilter,
  type CatalogFreshnessItem,
  type CatalogFreshnessPage,
  type CatalogFreshnessProvider,
  type CatalogFreshnessStatus
} from "@/lib/contracts/catalog-freshness";
import { getCatalogFreshnessPage } from "../_lib/freshness-api";

type FilterCopy = {
  readonly label: string;
  readonly heading: string;
  readonly empty: string;
};

// Prosa (encabezados y estados vacíos) frente a la etiqueta corta del distintivo. Las horas son las que el
// servidor usó para clasificar; hasta la primera respuesta se usa el valor del contrato (72 h), nunca la TTL de
// proveedor de una semana.
function windowPhrase(hours: number): string {
  return `${hours} h o menos`;
}

function filterCopy(filter: CatalogFreshnessFilter, hours: number): FilterCopy {
  switch (filter) {
    case "recent":
      return {
        label: freshnessShortStatusLabel("recent", hours),
        heading: `Con un precio más barato guardado de ${windowPhrase(hours)}`,
        empty: `Ningún juego de tu lista de deseos tiene un precio más barato guardado de ${windowPhrase(hours)}.`
      };
    case "stale":
      return {
        label: freshnessShortStatusLabel("stale", hours),
        heading: `Cuyo precio más barato guardado es más antiguo que ${hours} h`,
        empty: "Ningún juego de tu lista de deseos tiene un precio más barato guardado más antiguo que la ventana."
      };
    case "missing":
      return {
        label: freshnessShortStatusLabel("missing", hours),
        heading: "Juegos sin precio comparable en MXN",
        empty: "Todos los juegos de tu lista de deseos tienen un precio comparable guardado."
      };
    default:
      return {
        label: "Toda mi lista de deseos",
        heading: "Todos los juegos de tu lista de deseos, una vez cada uno",
        empty: "Tu lista de deseos todavía no tiene juegos."
      };
  }
}

// `success` queda reservado a la mejor oferta (DESIGN §4): la actualización es metadato de datos, así que usa
// `info`, y la antigüedad —que no es un error— usa `warning` con su texto al lado.
const STATUS_TONE: Record<CatalogFreshnessStatus, string> = {
  recent: "tabler-badge-info",
  stale: "tabler-badge-warning",
  missing: "tabler-badge-muted"
};

const momentFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeStyle: "short" });

function formatMoment(value: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : momentFormatter.format(date);
}

// Un contador ilegible se lee «—»: el panel no convierte lo desconocido en «no hay juegos».
function formatCount(value: number | null): string {
  return value === null ? "—" : String(value);
}

// Portada pequeña de la tarjeta: 16:9 dentro del ancho de la columna, decorativa (`alt=""`) porque el nombre
// está al lado, con `Gamepad2` cuando la URL falta o falla.
function FreshnessThumb({ src }: { readonly src: string | null }) {
  const [failed, setFailed] = useState(false);
  const image = src && !failed ? src : null;

  return (
    <span className="flex aspect-video w-full items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)]">
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
        <Gamepad2 className="h-5 w-5 text-muted" aria-hidden="true" />
      )}
    </span>
  );
}

// Dos fechas distintas, nunca una sola: «precio observado» es la última respuesta con precio de ESA tienda y
// «consultado» es la última consulta. Una consulta sin precio deja el precio desconocido, no en cero.
function ProviderDates({ entry }: { readonly entry: CatalogFreshnessProvider }) {
  const observed = formatMoment(entry.priceObservedAtUtc);
  const checked = formatMoment(entry.lastCheckedAtUtc);

  return (
    <li className="text-xs text-muted">
      <span className="font-semibold text-secondary">{freshnessSourceLabel(entry.provider)}</span>{" "}
      <span>{observed === null ? "Precio observado: sin fecha guardada" : `Precio observado: ${observed}`}</span>
      {" · "}
      <span>{checked === null ? "Consultado: sin fecha guardada" : `Consultado: ${checked}`}</span>
    </li>
  );
}

// Tarjeta compacta de un juego: portada pequeña, título en una línea, el estado corto y el precio GANADOR con
// su tienda y su fecha. El detalle por proveedor queda colapsado: es información de segundo nivel y con seis
// columnas en escritorio una lista siempre visible convertiría la página en una tabla de fechas.
function FreshnessCard({ item, hours }: { readonly item: CatalogFreshnessItem; readonly hours: number }) {
  // Los tres salen de la MISMA oferta ganadora: su fecha, su monto y su tienda. Nunca se combinan filas.
  const observed = formatMoment(item.priceObservedAtUtc);
  const price = formatChosenOfferPrice(item.chosenOffer);
  const store = chosenOfferStoreLabel(item.chosenOffer);

  return (
    <article className="app-card flex h-full flex-col gap-2 p-2.5">
      <FreshnessThumb src={item.imageUrl} />

      <div className="min-w-0 space-y-1">
        {item.steamAppId === null ? (
          <span className="line-clamp-1 text-sm font-semibold text-primary">{item.name}</span>
        ) : (
          <Link
            href={`/games/${item.steamAppId}`}
            className="block line-clamp-1 text-sm font-semibold text-primary hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            {item.name}
          </Link>
        )}
        <div className="flex flex-wrap items-center gap-1">
          <span className={cn("tabler-badge", STATUS_TONE[item.status])}>
            {freshnessShortStatusLabel(item.status, hours)}
          </span>
          {item.steamAppId === null ? <span className="text-xs text-muted">Sin ficha en Steam</span> : null}
        </div>
      </div>

      <div className="space-y-0.5">
        {price === null ? (
          <p className="text-xs text-muted">
            {item.status === "missing"
              ? "Sin precio comparable guardado en MXN; eso no prueba que la consulta falló."
              : "El monto de ese precio no está guardado."}
          </p>
        ) : (
          <p className="flex flex-wrap items-baseline gap-x-1.5">
            <span className="deal-price text-base text-primary">{price}</span>
            {store === null ? null : <span className="min-w-0 truncate text-xs text-secondary">{store}</span>}
          </p>
        )}
        {observed === null ? null : <p className="text-xs text-muted">{`Guardado ${observed}`}</p>}
      </div>

      {item.providers.length === 0 ? (
        <p className="mt-auto text-xs text-muted">Sin fechas por proveedor guardadas.</p>
      ) : (
        <details className="mt-auto border-t border-default pt-1.5">
          <summary className="cursor-pointer text-xs font-semibold text-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]">
            {`Fechas por proveedor (${item.providers.length})`}
          </summary>
          <ul className="space-y-1 pt-1.5">
            {item.providers.map((entry) => (
              <ProviderDates key={entry.provider} entry={entry} />
            ))}
          </ul>
        </details>
      )}
    </article>
  );
}

type Summary = {
  readonly counts: CatalogFreshnessCounts;
  readonly asOfUtc: string | null;
  readonly freshnessHours: number;
  // Contador ADITIVO del sobre: entradas de la lista sin juego/tienda reconocidos. No son filas con identidad
  // y no entran en las cubetas; desconocido (`null`) no es 0.
  readonly unresolvedWishlistEntries: number | null;
};

export function CatalogFreshnessPanel() {
  const [filter, setFilter] = useState<CatalogFreshnessFilter>("all");
  const [page, setPage] = useState(1);
  const [pageData, setPageData] = useState<CatalogFreshnessPage | null>(null);
  // Los contadores y el corte sobreviven a un error y a un cambio de filtro: el sobre los trae todos juntos.
  const [summary, setSummary] = useState<Summary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const generation = useRef(0);
  const inFlight = useRef<AbortController | null>(null);

  const load = useCallback(async (targetFilter: CatalogFreshnessFilter, targetPage: number) => {
    const request = ++generation.current;
    inFlight.current?.abort();
    const controller = new AbortController();
    inFlight.current = controller;

    setLoading(true);
    setError(null);

    try {
      const data = await getCatalogFreshnessPage(
        targetFilter,
        targetPage,
        CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT,
        controller.signal
      );
      if (request !== generation.current) return;
      setPageData(data);
      setSummary({
        counts: data.counts,
        asOfUtc: data.asOfUtc,
        freshnessHours: data.freshnessHours,
        unresolvedWishlistEntries: data.unresolvedWishlistEntries
      });
      setFilter(data.filter);
      setPage(data.page);
    } catch (cause) {
      if (request !== generation.current) return;
      // Cancelar el pedido anterior no es un error que el usuario deba ver.
      if (controller.signal.aborted) return;
      setError(cause instanceof Error ? cause.message : "No se pudo cargar el estado de actualización de precios.");
    } finally {
      if (request === generation.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load("all", 1);
    // Solo la primera carga: los cambios posteriores pasan por los controles del panel.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => () => inFlight.current?.abort(), []);

  function selectFilter(next: CatalogFreshnessFilter) {
    if (next === filter && !error) return;
    // El filtro responde al instante; la lista se pinta solo cuando la respuesta coincide con él.
    setFilter(next);
    setPage(1);
    void load(next, 1);
  }

  function goToPage(next: number) {
    setPage(next);
    void load(filter, next);
  }

  const showing = pageData !== null && pageData.filter === filter && pageData.page === page ? pageData : null;
  const hours = showing?.freshnessHours ?? summary?.freshnessHours ?? CATALOG_FRESHNESS_HOURS;
  const counts = summary?.counts ?? null;
  // El aviso se decide SOLO por el contador aparte: aunque el catálogo canónico esté en 0, N entradas sin
  // identificar > 0 tiene que verse. Un 0 conocido o un desconocido no inventan aviso.
  const unresolved = unresolvedWishlistEntriesCount(summary);
  const asOf = formatMoment(showing?.asOfUtc ?? summary?.asOfUtc ?? null);
  const copy = filterCopy(filter, hours);
  const totalPages = showing === null ? 1 : Math.max(1, Math.ceil(showing.total / showing.pageSize));
  // Una sola espera visible: primera carga y cambio de filtro/página son el mismo estado.
  const isWaiting = loading && showing === null && error === null;

  return (
    <section className="app-card space-y-4 p-5" aria-labelledby="update-status-heading">
      <div className="space-y-2">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">Lista de deseos</p>
        <h2 id="update-status-heading" className="text-xl font-semibold tracking-tight text-primary">
          Estado de actualización de precios
        </h2>
        <p className="text-xs text-muted">
          Estado y fecha del precio más barato guardado; no implica oferta vigente. Cada juego de tu lista de
          deseos aparece una vez y se clasifica por la fecha de ese precio: {freshnessShortStatusLabel("recent", hours)}{" "}
          si está dentro de la ventana, {freshnessShortStatusLabel("stale", hours)} si es más antiguo, y «
          {freshnessShortStatusLabel("missing", hours)}» si no hay ninguno. Antiguo no es erróneo —una fecha vieja
          solo dice que ese precio no se volvió a observar— y «sin precio» no prueba que la consulta falló.
        </p>
        <p className="text-xs text-muted">
          Las fechas por proveedor son las guardadas para tu lista, y no todas las tiendas se refrescan a la vez.
          Esta sección y la pestaña «Menos de MX$50» no cuentan lo mismo: aquí manda la fecha del precio más barato
          guardado, mientras que esa pestaña cuenta las ofertas que hoy cumplen su regla de precio bajo, así que un
          juego puede aparecer en una y no en la otra sin que ninguna de las dos cifras sea un error.
        </p>
      </div>

      <div className="flex flex-wrap gap-2" role="group" aria-label="Filtros de actualización de precios">
        {CATALOG_FRESHNESS_FILTERS.map((option) => {
          // Los cuatro contadores vienen del mismo sobre: pintarlos no dispara un pedido por filtro.
          const count = freshnessCountFromCounts(counts, option);
          const optionCopy = filterCopy(option, hours);
          return (
            <button
              key={option}
              type="button"
              aria-pressed={option === filter}
              aria-label={
                count === null ? optionCopy.label : `${optionCopy.label}: ${count} ${count === 1 ? "juego" : "juegos"}`
              }
              onClick={() => selectFilter(option)}
              className={cn(
                "inline-flex h-9 items-center gap-1.5 rounded-[var(--radius-sm)] border px-3 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                option === filter
                  ? "border-accent bg-[var(--color-accent-soft)] text-primary"
                  : "border-default text-secondary hover:text-primary"
              )}
            >
              {optionCopy.label}
              <span className={cn("tabler-badge", option === filter ? "tabler-badge-primary" : "tabler-badge-muted")}>
                {formatCount(count)}
              </span>
            </button>
          );
        })}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Button
          type="button"
          variant="secondary"
          onClick={() => void load(filter, page)}
          loading={loading}
          loadingText="Actualizando..."
        >
          <RotateCw className="h-4 w-4" aria-hidden="true" />
          Actualizar
        </Button>
        {asOf === null ? null : <span className="tabler-badge tabler-badge-info">{`Corte ${asOf}`}</span>}
        {showing === null ? null : (
          <span className="tabler-badge tabler-badge-muted">
            {showing.total === 1 ? "1 juego" : `${showing.total} juegos`}
          </span>
        )}
        {showing === null ? null : (
          <span className="tabler-badge tabler-badge-muted">{`Página ${showing.page} de ${totalPages}`}</span>
        )}
      </div>

      {hasUnresolvedWishlistEntries(summary) ? (
        <p
          role="status"
          className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3 text-xs text-secondary"
        >
          {`Entradas de tu lista de deseos sin identificar: ${formatCount(unresolved)}. No tienen un juego ni una tienda reconocidos, así que no se cuentan en las cubetas de arriba y el refresco manual no puede actualizarlas hasta que su identidad se resuelva.`}
        </p>
      ) : null}

      <p className="text-xs text-muted">
        {"El refresco manual se lanza desde "}
        <Link
          href="/jobs"
          className="font-semibold text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
        >
          Jobs
        </Link>
        {
          " con el job «catalog.prices-refresh» (solo administradores). Procesa solo pendientes de tu lista de deseos, en un lote de hasta 20 juegos por ejecución, así que puede hacer falta lanzarlo más de una vez. Un juego sin ficha en Steam ni identidad de tienda soportada puede no actualizarse: el job lo reporta como no soportado en vez de inventar un precio. Esta sección no ejecuta nada: solo muestra lo ya guardado."
        }
      </p>

      <p className="sr-only" aria-live="polite">
        {isWaiting
          ? "Cargando el estado de actualización de precios."
          : showing !== null
            ? `${copy.label}: ${showing.count} juegos en la página ${showing.page} de ${totalPages}, de ${showing.total}.`
            : error !== null
              ? "No se pudo cargar el estado de actualización de precios."
              : "Cargando el estado de actualización de precios."}
      </p>

      <div className="space-y-1">
        <p className="text-xs font-semibold uppercase tracking-widest text-muted">{copy.label}</p>
        <h3 className="text-lg font-semibold tracking-tight text-primary">{copy.heading}</h3>
      </div>

      {error !== null ? (
        <div className="space-y-3">
          <Alert variant="danger">{error}</Alert>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="secondary" onClick={() => void load(filter, page)} loading={loading}>
              Reintentar
            </Button>
            <Button
              type="button"
              variant="ghost"
              onClick={() => void load(filter, 1)}
              disabled={loading || page === 1}
            >
              Volver a la primera página
            </Button>
          </div>
          <p className="text-xs text-muted">
            Los contadores siguen siendo los de la última lectura válida: el error es de esta página, no de la
            sección.
          </p>
        </div>
      ) : null}

      {isWaiting ? <p className="text-sm text-muted">Cargando...</p> : null}

      {showing !== null && showing.items.length === 0 ? (
        <p className="text-sm text-muted">{copy.empty}</p>
      ) : null}

      {showing !== null && showing.items.length > 0 ? (
        <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6">
          {showing.items.map((item) => (
            <li key={item.gameId} className="min-w-0">
              <FreshnessCard item={item} hours={hours} />
            </li>
          ))}
        </ul>
      ) : null}

      {showing !== null && showing.items.length > 0 ? (
        <nav className="flex flex-wrap items-center gap-2" aria-label="Paginación de la lista de deseos">
          <Button
            type="button"
            variant="secondary"
            onClick={() => goToPage(showing.page - 1)}
            disabled={loading || showing.page <= 1}
            aria-label="Página anterior de la lista de deseos"
          >
            <ChevronLeft className="h-4 w-4" aria-hidden="true" />
            Anterior
          </Button>
          <span className="tabler-badge tabler-badge-muted">{`Página ${showing.page} de ${totalPages}`}</span>
          <Button
            type="button"
            variant="secondary"
            onClick={() => goToPage(showing.page + 1)}
            disabled={loading || !showing.hasMore}
            aria-label="Página siguiente de la lista de deseos"
          >
            Siguiente
            <ChevronRight className="h-4 w-4" aria-hidden="true" />
          </Button>
        </nav>
      ) : null}
    </section>
  );
}
