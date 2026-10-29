"use client";

// Keys duplicadas (S5): juegos que llegaron en un bundle y ya estaban en la biblioteca en la misma
// plataforma. La regla del usuario fue «solo juegos duplicados»: se registra el juego, su plataforma y el
// bundle de origen, y no se guarda ningún código de activación. Una posesión en otra plataforma no cuenta
// como duplicado y el servidor ya lo resuelve así; aquí solo se pinta lo que manda.
import { useCallback, useEffect, useRef, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { storeLabel } from "@/lib/contracts/stores";
import type { BundlePurchaseDuplicateList } from "@/lib/contracts/bundle-purchases";
import { getBundlePurchaseDuplicates } from "../_lib/bundle-purchases-api";

type Props = {
  /** Cambia después de una compra: si aparecieron duplicados nuevos, la sección los vuelve a pedir. */
  readonly refreshToken: number;
};

const GGDEALS_SOURCE = "ggdeals";
const dateFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeZone: "UTC" });

// `DateOnly?` en el backend: si no hay fecha declarada se dice, no se rellena.
function formatCalendarDate(value: string | null): string {
  if (value === null) return "sin fecha declarada";
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(date.getTime()) ? value : dateFormatter.format(date);
}

export function BundlePurchaseDuplicates({ refreshToken }: Props) {
  const [list, setList] = useState<BundlePurchaseDuplicateList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);

  const load = useCallback(async () => {
    const request = ++requestId.current;
    setLoading(true);
    setError(null);
    try {
      const next = await getBundlePurchaseDuplicates();
      if (request !== requestId.current) return;
      setList(next);
    } catch (cause) {
      if (request !== requestId.current) return;
      setError(cause instanceof Error ? cause.message : "No se pudieron cargar los juegos duplicados.");
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
    <section className="app-card space-y-3 p-5" aria-labelledby="bundle-duplicates-heading">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Duplicados</p>
          <h2 id="bundle-duplicates-heading" className="text-xl font-semibold tracking-tight text-primary">
            Keys duplicadas
          </h2>
          <p className="text-xs text-muted">
            Juegos de un bundle que ya tenías en tu biblioteca en la misma plataforma. Se registra el juego,
            su plataforma y el bundle del que salió: no se guardan códigos de activación. Tenerlo en otra
            plataforma no cuenta como duplicado.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {list !== null ? (
            <span className="tabler-badge tabler-badge-muted">
              {list.count === 1 ? "1 juego duplicado" : `${list.count} juegos duplicados`}
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
          No hay juegos duplicados todavía. Cuando registres un bundle con un juego que ya estaba en tu
          biblioteca en la misma plataforma, aparecerá aquí.
        </p>
      ) : null}

      {list !== null && list.items.length > 0 ? (
        <ul className="space-y-2">
          {list.items.map((duplicate) => (
            <li
              key={`${duplicate.purchaseId}:${duplicate.itemKey}`}
              className="space-y-1.5 rounded-[var(--radius-md)] border border-default p-3"
            >
              <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                <p className="text-sm font-semibold text-primary">{duplicate.title}</p>
                <p className="text-xs text-muted">Compra: {formatCalendarDate(duplicate.acquiredAt)}</p>
              </div>
              <div className="flex flex-wrap items-center gap-1.5 text-xs">
                <span className="tabler-badge tabler-badge-muted">{storeLabel(duplicate.platform)}</span>
                <span className="tabler-badge tabler-badge-warning">
                  {duplicate.existingTitle === null
                    ? "Ya lo tenías en tu biblioteca"
                    : `Ya lo tenías como «${duplicate.existingTitle}»`}
                </span>
                <span
                  className={
                    duplicate.source === GGDEALS_SOURCE
                      ? "tabler-badge tabler-badge-warning"
                      : "tabler-badge tabler-badge-info"
                  }
                >
                  {duplicate.source === GGDEALS_SOURCE ? "GG.deals" : "ITAD"}
                </span>
              </div>
              <p className="text-xs text-muted">Bundle: {duplicate.bundleTitle}</p>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
