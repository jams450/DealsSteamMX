"use client";

// `/discover` (S1/S2/S3/S6): el catálogo conocido en tres pestañas independientes — menos de MX$50, mínimo
// verificado del proveedor y nota mayor a 7 — con el contador exacto de cada una y paginación acotada.
//
// Una sola petición por pestaña y página: el sobre del backend trae la lista seleccionada y los TRES
// contadores, así que pintar los números de las tres pestañas nunca dispara tres pedidos. Es el mismo catálogo
// global que alimenta los avisos de ntfy (wishlist y favoritos son la otra superficie, personal).
//
// Lo que este cliente NO hace:
//   · no llama a ningún proveedor: solo `GET /api/bff/discover`, que lee snapshots persistidos;
//   · no inventa la etiqueta de la pestaña: `normalizeDiscoverPage` toma la categoría pedida como única fuente,
//     y aquí solo se pinta la página que coincide con la pestaña Y la página activas, así que una respuesta
//     atrasada no puede rotular la lista con la regla de otra pestaña;
//   · no borra lo que ya está en pantalla: un error conserva la lista vigente y ofrece «Reintentar»; los
//     contadores de las tres pestañas se conservan porque vienen del último sobre válido.
//
// El pedido en vuelo se cancela con `AbortController` y se numera con una generación: un cambio rápido de
// pestaña o de página no puede dejar ganar a la respuesta vieja.
import { useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { ChevronLeft, ChevronRight, RotateCw } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import {
  DISCOVER_CATEGORIES,
  DISCOVER_PAGE_SIZE_DEFAULT,
  type DiscoverCategory,
  type DiscoverPage
} from "@/lib/contracts/discover";
import { DealCard } from "./_components/deal-card";
import { getDiscoverPage } from "./_lib/discover-api";

const ITAD_ATTRIBUTION_URL = "https://isthereanydeal.com";
const GGDEALS_ATTRIBUTION_URL = "https://gg.deals";

type CategoryCopy = {
  readonly label: string;
  readonly heading: string;
  readonly note: string;
  readonly empty: string;
};

// Cada pestaña es una regla, no un nivel: la nota no exige precio bajo y el mínimo no exige ser barato.
const CATEGORY_COPY: Record<DiscoverCategory, CategoryCopy> = {
  under50: {
    label: "Menos de MX$50",
    heading: "Precios por debajo de MX$50",
    note: "El precio MXN de la tienda que calificó está estrictamente por debajo de MX$50.00. Si esa cifra sale de una conversión, lleva «≈» y la etiqueta «Estimado».",
    empty: "Ninguna tienda guardada tiene un precio por debajo de MX$50 ahora mismo."
  },
  historic: {
    label: "Mínimo histórico",
    heading: "En el mínimo que reportó su proveedor",
    note: "El precio actual de esa misma fila iguala el mínimo que su proveedor reportó, en la moneda de la oferta. No es el mínimo local de Steam ni un récord nuevo.",
    empty: "Ninguna oferta está en el mínimo que reportó su proveedor."
  },
  score: {
    label: "Mejores ofertas",
    heading: "Ofertas con nota mayor a 7",
    note: "Nota de 0 a 10 con la fórmula de precio que ya existe: descuento frente al precio base de Steam más cercanía al mínimo del proveedor. No mide la calidad del juego.",
    empty: "Ninguna oferta supera la nota 7 ahora mismo."
  }
};

type Counts = Record<DiscoverCategory, number>;

function countsOf(page: DiscoverPage): Counts {
  return {
    under50: page.under50Total,
    historic: page.historicTotal,
    score: page.scoreTotal
  };
}

export function DiscoverClient() {
  const [category, setCategory] = useState<DiscoverCategory>("under50");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DISCOVER_PAGE_SIZE_DEFAULT);
  const [pageData, setPageData] = useState<DiscoverPage | null>(null);
  const [counts, setCounts] = useState<Counts | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const generation = useRef(0);
  const inFlight = useRef<AbortController | null>(null);

  const load = useCallback(
    async (targetCategory: DiscoverCategory, targetPage: number) => {
      const request = ++generation.current;
      inFlight.current?.abort();
      const controller = new AbortController();
      inFlight.current = controller;

      setLoading(true);
      setError(null);

      try {
        const data = await getDiscoverPage(targetCategory, targetPage, pageSize, controller.signal);
        if (request !== generation.current) return;
        setPageData(data);
        setCategory(data.category);
        setPage(data.page);
        setPageSize(data.pageSize);
        setCounts(countsOf(data));
      } catch (cause) {
        if (request !== generation.current) return;
        // Cancelar el pedido anterior no es un error que el usuario deba ver.
        if (controller.signal.aborted) return;
        setError(cause instanceof Error ? cause.message : "No se pudo cargar el descubrimiento.");
      } finally {
        if (request === generation.current) setLoading(false);
      }
    },
    [pageSize]
  );

  useEffect(() => {
    void load("under50", 1);
    // Solo la primera carga: los cambios posteriores pasan por los controles de la página.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => () => inFlight.current?.abort(), []);

  function selectCategory(next: DiscoverCategory) {
    if (next === category && !error) return;
    // La pestaña responde al instante; la lista se pinta solo cuando la respuesta coincide con ella.
    setCategory(next);
    setPage(1);
    void load(next, 1);
  }

  function onTabKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const index = DISCOVER_CATEGORIES.indexOf(category);
    let next = -1;
    if (event.key === "ArrowRight") next = (index + 1) % DISCOVER_CATEGORIES.length;
    else if (event.key === "ArrowLeft") next = (index - 1 + DISCOVER_CATEGORIES.length) % DISCOVER_CATEGORIES.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = DISCOVER_CATEGORIES.length - 1;
    else return;

    event.preventDefault();
    const tab = DISCOVER_CATEGORIES[next];
    selectCategory(tab);
    document.getElementById(`discover-tab-${tab}`)?.focus();
  }

  function goToPage(next: number) {
    setPage(next);
    void load(category, next);
  }

  const showing = pageData !== null && pageData.category === category && pageData.page === page ? pageData : null;
  const copy = CATEGORY_COPY[category];
  const totalPages = showing ? Math.max(1, Math.ceil(showing.total / showing.pageSize)) : 1;
  // Una sola espera visible: primera carga y cambio de pestaña/página son el mismo estado (no hay lista que
  // coincida con la pestaña activa).
  const isWaiting = loading && showing === null && error === null;

  return (
    <div className="space-y-4">
      <section className="app-card space-y-3 p-5" aria-labelledby="discover-heading">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Descubrimiento</p>
          <h2 id="discover-heading" className="text-xl font-semibold tracking-tight text-primary">
            Todo el catálogo conocido, por regla
          </h2>
          <p className="text-xs text-muted">
            Tres reglas independientes sobre precios ya observados: menos de MX$50, mínimo verificado del
            proveedor y nota mayor a 7. Un juego puede estar en varias, y el mínimo o la nota no exigen ser
            barato. Son las mismas reglas que envían los avisos, y aquí no se consulta ninguna tienda: la
            página solo lee lo que ya está guardado.
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
            <span aria-hidden="true">·</span>
            <a
              href={GGDEALS_ATTRIBUTION_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            >
              GG.deals
            </a>
          </p>
        </div>

        <div role="tablist" aria-label="Categorías de descubrimiento" onKeyDown={onTabKeyDown} className="flex flex-wrap gap-2">
          {DISCOVER_CATEGORIES.map((tab) => (
            <button
              key={tab}
              type="button"
              role="tab"
              id={`discover-tab-${tab}`}
              aria-selected={tab === category}
              aria-controls={`discover-panel-${tab}`}
              aria-label={counts ? `${CATEGORY_COPY[tab].label}: ${counts[tab]} juegos` : CATEGORY_COPY[tab].label}
              tabIndex={tab === category ? 0 : -1}
              onClick={() => selectCategory(tab)}
              className={cn(
                "inline-flex h-9 items-center gap-1.5 rounded-[var(--radius-sm)] border px-3 text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                tab === category
                  ? "border-accent bg-[var(--color-accent-soft)] text-primary"
                  : "border-default text-secondary hover:text-primary"
              )}
            >
              {CATEGORY_COPY[tab].label}
              <span className={cn("tabler-badge", tab === category ? "tabler-badge-primary" : "tabler-badge-muted")}>
                {counts ? counts[tab] : "—"}
              </span>
            </button>
          ))}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            type="button"
            variant="secondary"
            onClick={() => void load(category, page)}
            loading={loading}
            loadingText="Actualizando..."
          >
            <RotateCw className="h-4 w-4" aria-hidden="true" />
            Actualizar
          </Button>
          {showing ? (
            <span className="tabler-badge tabler-badge-muted">
              {showing.total === 1 ? "1 juego" : `${showing.total} juegos`}
            </span>
          ) : null}
          {showing ? (
            <span className="tabler-badge tabler-badge-muted">
              {`Página ${showing.page} de ${totalPages}`}
            </span>
          ) : null}
        </div>

        <p className="sr-only" aria-live="polite">
          {isWaiting
            ? "Cargando descubrimiento."
            : showing
              ? `${copy.label}: ${showing.count} tarjetas en la página ${showing.page} de ${totalPages}, de ${showing.total} juegos.`
              : error
                ? "No se pudo cargar esta pestaña."
                : "Cargando pestaña."}
        </p>
      </section>

      {DISCOVER_CATEGORIES.map((tab) => (
        <div
          key={tab}
          role="tabpanel"
          id={`discover-panel-${tab}`}
          aria-labelledby={`discover-tab-${tab}`}
          tabIndex={0}
          hidden={tab !== category}
          className="space-y-3 focus-visible:outline-none"
        >
          {tab !== category ? null : (
            <>
              <div className="space-y-1">
                <p className="text-xs font-semibold uppercase tracking-widest text-muted">{copy.label}</p>
                <h3 className="text-xl font-semibold tracking-tight text-primary">{copy.heading}</h3>
                <p className="text-xs text-muted">{copy.note}</p>
              </div>

              {error ? (
                <div className="space-y-3">
                  <Alert variant="danger">{error}</Alert>
                  <div className="flex flex-wrap gap-2">
                    <Button type="button" variant="secondary" onClick={() => void load(category, page)} loading={loading}>
                      Reintentar
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={() => void load(category, 1)}
                      disabled={loading || page === 1}
                    >
                      Volver a la primera página
                    </Button>
                  </div>
                  <p className="text-xs text-muted">
                    Las otras pestañas siguen disponibles con sus contadores: el error es de esta página, no de la
                    sección.
                  </p>
                </div>
              ) : null}

              {isWaiting ? (
                <p className="app-card p-5 text-sm text-muted">Cargando...</p>
              ) : null}

              {showing && showing.items.length === 0 ? (
                <p className="app-card p-5 text-sm text-muted">{copy.empty}</p>
              ) : null}

              {showing && showing.items.length > 0 ? (
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                  {showing.items.map((item) => (
                    <DealCard key={item.gameId} item={item} />
                  ))}
                </div>
              ) : null}

              {showing && showing.items.length > 0 ? (
                <nav className="flex flex-wrap items-center gap-2" aria-label="Paginación del descubrimiento">
                  <Button
                    type="button"
                    variant="secondary"
                    onClick={() => goToPage(showing.page - 1)}
                    disabled={loading || showing.page <= 1}
                    aria-label="Página anterior"
                  >
                    <ChevronLeft className="h-4 w-4" aria-hidden="true" />
                    Anterior
                  </Button>
                  <span className="tabler-badge tabler-badge-muted">
                    {`Página ${showing.page} de ${totalPages}`}
                  </span>
                  <Button
                    type="button"
                    variant="secondary"
                    onClick={() => goToPage(showing.page + 1)}
                    disabled={loading || !showing.hasMore}
                    aria-label="Página siguiente"
                  >
                    Siguiente
                    <ChevronRight className="h-4 w-4" aria-hidden="true" />
                  </Button>
                </nav>
              ) : null}
            </>
          )}
        </div>
      ))}
    </div>
  );
}
