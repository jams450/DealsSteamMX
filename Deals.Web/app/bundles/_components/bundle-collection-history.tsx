"use client";

// Historial de compras de bundles (S3/S5). Solo lectura del endpoint del usuario autenticado: cada fila es
// una compra registrada con su tier, su fecha declarada y lo que pasó con sus juegos. No se muestran —ni se
// piden— códigos de activación; tampoco se pintan «keys»: el contrato registra juegos y plataformas.
import { useCallback, useEffect, useRef, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { storeLabel } from "@/lib/contracts/stores";
import type { BundlePurchaseList } from "@/lib/contracts/bundle-purchases";
import { getBundlePurchases } from "../_lib/bundle-purchases-api";

type Props = {
  /** Cambia después de una compra: la sección vuelve a pedir su lista para no quedar desactualizada. */
  readonly refreshToken: number;
};

const GGDEALS_SOURCE = "ggdeals";
const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeZone: "UTC" });

// `DateOnly?` en el backend: una compra puede no tener fecha declarada. Se dice, no se inventa.
function formatCalendarDate(value: string | null): string {
  if (value === null) return "sin fecha declarada";
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(date.getTime()) ? value : dateFormatter.format(date);
}

export function BundlePurchaseHistory({ refreshToken }: Props) {
  const [list, setList] = useState<BundlePurchaseList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);

  const load = useCallback(async () => {
    const request = ++requestId.current;
    setLoading(true);
    setError(null);
    try {
      const next = await getBundlePurchases();
      if (request !== requestId.current) return;
      setList(next);
    } catch (cause) {
      if (request !== requestId.current) return;
      // Un error conserva lo que ya está en pantalla; el botón de reintento vuelve a pedirlo.
      setError(cause instanceof Error ? cause.message : "No se pudo cargar tu historial de compras.");
    } finally {
      if (request === requestId.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshToken]);

  useEffect(
    () => () => {
      requestId.current += 1;
    },
    []
  );

  const isInitialLoading = loading && list === null && error === null;

  return (
    <section className="app-card space-y-3 p-5" aria-labelledby="bundle-history-heading">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Compras</p>
          <h2 id="bundle-history-heading" className="text-xl font-semibold tracking-tight text-primary">
            Historial de bundles comprados
          </h2>
          <p className="text-xs text-muted">
            Bundles que registraste con su tier y su fecha. Los juegos ya se importaron a tu biblioteca; los
            que ya tenías en la misma plataforma quedaron en Keys duplicadas.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {list !== null ? (
            <span className="tabler-badge tabler-badge-muted">
              {list.count === 1 ? "1 compra" : `${list.count} compras`}
            </span>
          ) : null}
          <Button type="button" variant="secondary" loading={loading} loadingText="Actualizando..." onClick={() => void load()}>
            Actualizar
          </Button>
        </div>
      </div>

      {error ? (
        <div className="space-y-2">
          <Alert variant="danger">{error}</Alert>
          <Button type="button" variant="secondary" onClick={() => void load()} loading={loading}>
            Reintentar
          </Button>
        </div>
      ) : null}

      {isInitialLoading ? <p className="text-sm text-muted">Cargando...</p> : null}

      {list !== null && list.items.length === 0 && error === null ? (
        <p className="text-sm text-muted">
          Todavía no registraste ninguna compra de bundle. Cada bundle pendiente tiene su botón para
          registrarla.
        </p>
      ) : null}

      {list !== null && list.items.length > 0 ? (
        <ul className="space-y-2">
          {list.items.map((purchase) => (
            <li key={purchase.purchaseId} className="space-y-1.5 rounded-[var(--radius-md)] border border-default p-3">
              <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                <p className="text-sm font-semibold text-primary">{purchase.title}</p>
                <p className="text-xs text-muted">Compra: {formatCalendarDate(purchase.acquiredAt)}</p>
              </div>
              <div className="flex flex-wrap items-center gap-1.5 text-xs">
                <span
                  className={
                    purchase.source === GGDEALS_SOURCE
                      ? "tabler-badge tabler-badge-warning"
                      : "tabler-badge tabler-badge-info"
                  }
                >
                  {purchase.source === GGDEALS_SOURCE ? "GG.deals" : "ITAD"}
                </span>
                <span className="tabler-badge tabler-badge-muted">{storeLabel(purchase.platform)}</span>
                <span className="tabler-badge tabler-badge-success">
                  {purchase.importedCount === 1 ? "1 juego importado" : `${purchase.importedCount} juegos importados`}
                </span>
                {purchase.duplicateCount > 0 ? (
                  <span className="tabler-badge tabler-badge-warning">
                    {purchase.duplicateCount === 1
                      ? "1 duplicado"
                      : `${purchase.duplicateCount} duplicados`}
                  </span>
                ) : null}
                <span className="tabler-badge tabler-badge-muted">
                  {purchase.gamesCount === 1 ? "1 juego reportado" : `${purchase.gamesCount} juegos reportados`}
                </span>
              </div>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
