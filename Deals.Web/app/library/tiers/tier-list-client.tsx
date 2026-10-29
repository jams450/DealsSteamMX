"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import { GAME_TIERS, normalizeGameTierList, normalizeGameTierYears, type GameTier, type GameTierEntry } from "@/lib/contracts/game-tiers";
import { storeLabel } from "@/lib/contracts/stores";
import { applyTierPlacement, createVersionGate, moveTierEntry, sortTierEntries } from "./_lib/tier-list";

// Columnas del tablero. "Sin clasificar" no es un rango guardable: es la tarjeta todavía sin puesto.
const COLUMNS = [...GAME_TIERS, "Unranked"] as const;

type LoadError = { readonly scope: "years" | "entries" | "save"; readonly message: string };

function errorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error && cause.message ? cause.message : fallback;
}

export function TierListClient() {
  // Los años se piden primero: sin ellos no hay selector ni año por defecto. El más reciente manda.
  const [years, setYears] = useState<readonly number[] | null>(null);
  const [year, setYear] = useState<number | null>(null);
  const [entries, setEntries] = useState<readonly GameTierEntry[]>([]);
  const [loadingYears, setLoadingYears] = useState(true);
  const [loadingEntries, setLoadingEntries] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<LoadError | null>(null);
  const [notice, setNotice] = useState("");
  const [dragged, setDragged] = useState<number | null>(null);
  const [dragOver, setDragOver] = useState<string | null>(null);
  const [yearsReload, setYearsReload] = useState(0);
  const [entriesReload, setEntriesReload] = useState(0);

  const yearsGate = useRef(createVersionGate());
  const entriesGate = useRef(createVersionGate());
  const savesGate = useRef(createVersionGate());
  const savingRef = useRef(false);
  const yearRef = useRef<number | null>(null);
  const lastEntryYear = useRef<number | null>(null);

  // Carga de años: una respuesta vieja (otro reintento) no puede pisar la lista vigente.
  useEffect(() => {
    const ticket = yearsGate.current.begin();
    let active = true;
    setLoadingYears(true);
    setError((current) => (current?.scope === "years" ? null : current));
    void (async () => {
      try {
        const response = await fetch("/api/bff/game-tiers/years", { cache: "no-store" });
        if (!response.ok) throw await parseApiError(response, "No se pudieron cargar los años con reseñas");
        const list = normalizeGameTierYears(await response.json());
        if (list === null) throw new Error("El servidor devolvió una lista de años inválida");
        if (!active || !yearsGate.current.isCurrent(ticket)) return;
        setYears(list);
        // Conserva el año elegido si sigue existiendo; si no, el más reciente; sin reseñas, ninguno.
        setYear((current) => (current !== null && list.includes(current) ? current : list[0] ?? null));
        if (list.length === 0) {
          setEntries([]);
          setLoadingEntries(false);
        }
      } catch (cause) {
        if (active && yearsGate.current.isCurrent(ticket)) {
          setError({ scope: "years", message: errorMessage(cause, "No se pudieron cargar los años con reseñas") });
        }
      } finally {
        if (active && yearsGate.current.isCurrent(ticket)) setLoadingYears(false);
      }
    })();
    return () => { active = false; };
  }, [yearsReload]);

  // Carga de tarjetas del año: mismo guardia, para que cambiar de año rápido no mezcle listas.
  useEffect(() => {
    yearRef.current = year;
    if (year === null) {
      setEntries([]);
      setLoadingEntries(false);
      return;
    }
    const targetYear = year;
    // Al cambiar de año no se muestra la lista anterior bajo el año nuevo; al reintentar el mismo año la
    // lista vigente se conserva hasta que llegue la respuesta.
    if (lastEntryYear.current !== targetYear) setEntries([]);
    lastEntryYear.current = targetYear;
    const ticket = entriesGate.current.begin();
    let active = true;
    setLoadingEntries(true);
    setNotice("");
    setError((current) => (current?.scope === "save" ? current : null));
    void (async () => {
      try {
        const response = await fetch(`/api/bff/game-tiers?year=${targetYear}`, { cache: "no-store" });
        if (!response.ok) throw await parseApiError(response, "No se pudo cargar la tier list");
        const list = normalizeGameTierList(await response.json());
        if (list === null) throw new Error("El servidor devolvió una tier list inválida");
        if (!active || !entriesGate.current.isCurrent(ticket) || yearRef.current !== targetYear) return;
        setEntries(list);
      } catch (cause) {
        if (active && entriesGate.current.isCurrent(ticket) && yearRef.current === targetYear) {
          setError({ scope: "entries", message: errorMessage(cause, "No se pudo cargar la tier list") });
        }
      } finally {
        if (active && entriesGate.current.isCurrent(ticket)) setLoadingEntries(false);
      }
    })();
    return () => { active = false; };
  }, [year, entriesReload]);

  const retry = useCallback(() => {
    if (error?.scope === "years") setYearsReload((current) => current + 1);
    else setEntriesReload((current) => current + 1);
  }, [error]);

  async function move(reviewId: number, tier: GameTier) {
    if (savingRef.current || loadingEntries || year === null) return;
    const targetYear = year;
    const moved = moveTierEntry(entries, reviewId, tier);
    // Nada que guardar: la tarjeta no existe o ya está en ese rango.
    if (moved === null) return;
    const card = entries.find((entry) => entry.reviewId === reviewId);
    const ticket = savesGate.current.begin();
    savingRef.current = true;
    setSaving(true);
    setError(null);
    setNotice("");
    try {
      const response = await csrfFetch(`/api/bff/game-tiers?year=${targetYear}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ reviewId, tier, position: moved.position }),
        cache: "no-store"
      });
      // Guardado viejo: el usuario ya cambió de año o hay un guardado posterior. No se aplica ni avisa.
      if (!savesGate.current.isCurrent(ticket) || yearRef.current !== targetYear) return;
      if (!response.ok) throw await parseApiError(response, "No se pudo guardar la clasificación");
      setEntries((current) => applyTierPlacement(current, reviewId, tier, moved.position));
      setNotice(`${card?.title ?? "Partida"}: guardado en rango ${tier}`);
    } catch (cause) {
      if (savesGate.current.isCurrent(ticket) && yearRef.current === targetYear) {
        setError({ scope: "save", message: errorMessage(cause, "No se pudo guardar la clasificación") });
      }
    } finally {
      savingRef.current = false;
      setSaving(false);
      setDragged(null);
    }
  }

  const busy = loadingYears || loadingEntries || saving;
  const status = loadingYears ? "Cargando años…" : loadingEntries ? "Cargando…" : saving ? "Guardando…" : notice;

  return <section className="app-card space-y-4 p-4">
    <div className="flex flex-wrap items-end gap-3">
      <Select
        id="tier-year"
        label="Año jugado"
        value={year ?? ""}
        disabled={busy || years === null || years.length === 0}
        className="w-32"
        onChange={(event) => {
          const next = Number(event.target.value);
          if (Number.isInteger(next) && years?.includes(next)) setYear(next);
        }}
      >
        {years === null || years.length === 0 ? <option value="">Sin años</option> : null}
        {(years ?? []).map((option) => <option key={option} value={option}>{option}</option>)}
      </Select>
      <p className="text-sm text-muted">
        El año sale del mes de fin de la partida, o del de inicio si no hay fin.
      </p>
    </div>
    <p className="text-sm text-muted">
      Arrastra una partida a un rango o usa su selector. Cada cambio se guarda en tu cuenta. Cada partida reseñada es una tarjeta:
      si jugaste el mismo juego dos veces en el año, cada partida tiene su propio rango.
    </p>
    <p role="status" aria-live="polite">{status}</p>
    {error ? <div className="space-y-2"><Alert variant="danger">{error.message}</Alert><Button type="button" variant="secondary" onClick={retry}>Reintentar</Button></div> : null}
    {!loadingYears && years !== null && years.length === 0 ? <p>Todavía no tienes reseñas con fecha. Registra una reseña con mes de inicio o de fin para armar tu tier list.</p> : null}
    {!loadingYears && years !== null && years.length > 0 && !loadingEntries && !error && entries.length === 0 ? <p>No hay partidas reseñadas en {year}.</p> : null}
    {!loadingYears && years !== null && year !== null && (!loadingEntries || entries.length > 0) && COLUMNS.map((column) => <section key={column} aria-label={column === "Unranked" ? "Sin clasificar" : `Rango ${column}`} className={`flex min-w-0 flex-col gap-3 rounded-lg border border-default p-3 sm:flex-row${dragOver === column ? " tier-row-drop-target" : ""}`} onDragOver={(event) => {
      if (column === "Unranked" || dragged === null || busy) return;
      event.preventDefault();
      setDragOver(column);
    }} onDragLeave={() => setDragOver((current) => (current === column ? null : current))} onDrop={(event) => {
      event.preventDefault();
      setDragOver(null);
      if (column !== "Unranked" && dragged !== null) void move(dragged, column);
    }}>
      <h2 className="w-28 shrink-0 text-lg font-bold">{column === "Unranked" ? "Sin clasificar" : column}</h2>
      <div className="flex min-w-0 flex-1 flex-wrap gap-3">
        {sortTierEntries(entries.filter((entry) => entry.tier === column)).map((entry) => {
          const platform = storeLabel(entry.platform);
          return <article key={entry.reviewId} draggable={!busy} onDragStart={(event) => {
            setDragged(entry.reviewId);
            event.dataTransfer.setData("text/plain", String(entry.reviewId));
            event.dataTransfer.effectAllowed = "move";
          }} onDragEnd={() => { setDragged(null); setDragOver(null); }} aria-label={`${entry.title}, ${platform}${entry.isGoty ? ", GOTY" : ""}, partida ${entry.reviewId}`} className={`w-40 space-y-2 rounded bg-[var(--color-surface-2)] p-2 ${entry.isGoty ? "border-2 border-[color:var(--color-success)]" : "border border-default"}`}>
            {entry.imageUrl ? <img src={entry.imageUrl} alt="" loading="lazy" className="h-20 w-full object-cover" /> : null}
            <p className="break-words text-sm font-semibold">{entry.title}</p>
            <div className="flex flex-wrap items-center gap-1">
              <span className="tabler-badge tabler-badge-neutral">{platform}</span>
              {entry.isGoty ? <span className="tabler-badge tabler-badge-success">GOTY</span> : null}
            </div>
            <Select aria-label={`Rango de ${entry.title} en ${platform}, partida ${entry.reviewId}`} value={entry.tier} disabled={busy} className="w-full" onChange={(event) => void move(entry.reviewId, event.target.value as GameTier)}>
              {entry.tier === "Unranked" ? <option value="Unranked" disabled>Sin clasificar</option> : null}
              {GAME_TIERS.map((option) => <option key={option} value={option}>{option}</option>)}
            </Select>
          </article>;
        })}
      </div>
    </section>)}
  </section>;
}
