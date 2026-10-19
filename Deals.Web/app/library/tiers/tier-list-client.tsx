"use client";

import { useEffect, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import { GAME_TIERS, normalizeGameTierList, type GameTier, type GameTierEntry } from "@/lib/contracts/game-tiers";

export function TierListClient() {
  const [year, setYear] = useState(new Date().getFullYear());
  const [entries, setEntries] = useState<readonly GameTierEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragged, setDragged] = useState<number | null>(null);
  const [dragOver, setDragOver] = useState<string | null>(null);
  const [notice, setNotice] = useState("");

  useEffect(() => {
    let active = true;
    setLoading(true);
    setEntries([]);
    setError(null);
    setNotice("");
    async function load() {
      try {
        const response = await fetch(`/api/bff/game-tiers?year=${year}`, { cache: "no-store" });
        if (!response.ok) throw await parseApiError(response, "No se pudo cargar la tier list");
        const list = normalizeGameTierList(await response.json());
        if (list === null) throw new Error("El servidor devolvió una tier list inválida");
        if (active) setEntries(list);
      } catch (cause) {
        if (active) setError(cause instanceof Error ? cause.message : "No se pudo cargar la tier list");
      } finally {
        if (active) setLoading(false);
      }
    }
    void load();
    return () => { active = false; };
  }, [year]);

  async function move(gameId: number, tier: GameTier) {
    if (saving || loading) return;
    const game = entries.find((entry) => entry.gameId === gameId);
    if (!game) return;
    const positions = entries.filter((entry) => entry.tier === tier).map((entry) => entry.position);
    const position = positions.length ? Math.max(...positions) + 1 : 0;
    setSaving(true);
    setError(null);
    setNotice("");
    try {
      const response = await csrfFetch(`/api/bff/game-tiers?year=${year}`, {
        method: "PUT", headers: { "content-type": "application/json" },
        body: JSON.stringify({ gameId, tier, position }), cache: "no-store"
      });
      if (!response.ok) throw await parseApiError(response, "No se pudo guardar la clasificación");
      setEntries((current) => current.map((entry) => entry.gameId === gameId ? { ...entry, tier, position } : entry));
      setNotice(`${game.title}: guardado en ${tier}`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo guardar la clasificación");
    } finally {
      setSaving(false);
      setDragged(null);
    }
  }

  return <section className="app-card space-y-4 p-4">
    <label className="flex items-center gap-3 text-sm font-semibold">Año jugado
      <input type="number" min={1000} max={9999} value={year} disabled={saving} className="w-28 rounded border border-default bg-[var(--color-surface-2)] p-2" onChange={(event) => {
        const next = Number(event.target.value);
        if (Number.isInteger(next) && next >= 1000 && next <= 9999) setYear(next);
      }} />
    </label>
    <p className="text-sm text-muted">Arrastra un juego a un rango o usa su selector. Cada cambio se guarda en tu cuenta. Los juegos aparecen una sola vez por año, aunque tengan varias reseñas o plataformas.</p>
    <p role="status" aria-live="polite">{loading ? "Cargando…" : saving ? "Guardando…" : notice}</p>
    {error ? <Alert variant="danger">{error}</Alert> : null}
    {!loading && !error && entries.length === 0 ? <p>No hay juegos con reseñas fechadas en {year}.</p> : null}
    {!loading && ([...GAME_TIERS, "Unranked"] as const).map((tier) => <section key={tier} aria-label={tier === "Unranked" ? "Sin clasificar" : `Rango ${tier}`} className={`flex min-w-0 flex-col gap-3 rounded-lg border border-default p-3 sm:flex-row${dragOver === tier ? " tier-row-drop-target" : ""}`} onDragOver={(event) => {
      if (tier === "Unranked" || dragged === null || saving) return;
      event.preventDefault();
      setDragOver(tier);
    }} onDragLeave={() => setDragOver((current) => (current === tier ? null : current))} onDrop={(event) => {
      event.preventDefault();
      setDragOver(null);
      if (tier !== "Unranked" && dragged !== null) void move(dragged, tier);
    }}>
      <h2 className="w-28 shrink-0 text-lg font-bold">{tier === "Unranked" ? "Sin clasificar" : tier}</h2>
      <div className="flex min-w-0 flex-1 flex-wrap gap-3">
        {entries.filter((entry) => entry.tier === tier).sort((a, b) => a.position - b.position || a.gameId - b.gameId).map((entry) => <article key={entry.gameId} draggable={!saving} onDragStart={(event) => {
          setDragged(entry.gameId);
          event.dataTransfer.setData("text/plain", String(entry.gameId));
          event.dataTransfer.effectAllowed = "move";
        }} onDragEnd={() => { setDragged(null); setDragOver(null); }} className="w-40 space-y-2 rounded border border-default bg-[var(--color-surface-2)] p-2">
          {entry.imageUrl ? <img src={entry.imageUrl} alt="" loading="lazy" className="h-20 w-full object-cover" /> : null}
          <p className="break-words text-sm font-semibold">{entry.title}</p>
          <select aria-label={`Rango de ${entry.title}`} value={entry.tier} disabled={saving} className="w-full rounded border border-default bg-[var(--color-surface-1)] p-2" onChange={(event) => void move(entry.gameId, event.target.value as GameTier)}>
            {entry.tier === "Unranked" ? <option value="Unranked" disabled>Sin clasificar</option> : null}
            {GAME_TIERS.map((option) => <option key={option} value={option}>{option}</option>)}
          </select>
        </article>)}
      </div>
    </section>)}
  </section>;
}
