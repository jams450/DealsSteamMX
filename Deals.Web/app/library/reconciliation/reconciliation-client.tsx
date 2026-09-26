"use client";

import { useCallback, useEffect, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import type { CrossStateCandidateGroup, CrossStateMember, MergeApplied } from "@/lib/contracts/games-merge";
import { listCrossStateCandidates, mergeGame } from "../duplicates/_lib/games-merge-api";

function errorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error ? cause.message : fallback;
}

type ReconciliationJob = {
  readonly candidateKey: string;
  readonly survivor: CrossStateMember;
  readonly absorbed: readonly CrossStateMember[];
};

type AppliedOperation = {
  readonly candidateKey: string;
  readonly absorbed: CrossStateMember;
  readonly survivor: CrossStateMember;
  readonly result: MergeApplied;
};

type OperationState = {
  readonly phase: "merging" | "done" | "error";
  readonly jobs: readonly ReconciliationJob[];
  readonly applied: readonly AppliedOperation[];
  readonly error: string | null;
};

function memberBlocked(member: CrossStateMember): string | null {
  return member.blocked ? `${member.title}: ${member.blockReason ?? "miembro bloqueado"}` : null;
}

function groupBlocked(group: CrossStateCandidateGroup): string | null {
  if (group.blocked) return group.blockReason ?? "El candidato está bloqueado.";
  const blocked = group.members.find((member) => member.blocked);
  return blocked ? memberBlocked(blocked) : null;
}

function movedSummary(result: MergeApplied): string {
  const parts: string[] = [];
  if (result.movedExternalIds !== null) parts.push(`${result.movedExternalIds} identificadores`);
  if (result.movedSteamGames !== null) parts.push(`${result.movedSteamGames} filas de Steam`);
  if (result.movedLibraryRows !== null) parts.push(`${result.movedLibraryRows} filas de biblioteca`);
  return parts.length > 0 ? parts.join(" · ") : "sin contadores informados";
}

export function ReconciliationClient() {
  const [groups, setGroups] = useState<readonly CrossStateCandidateGroup[] | null>(null);
  const [selectedSurvivors, setSelectedSurvivors] = useState<Record<string, number>>({});
  const [selectedAbsorbed, setSelectedAbsorbed] = useState<Record<string, Record<number, boolean>>>({});
  const [confirm, setConfirm] = useState<readonly ReconciliationJob[] | null>(null);
  const [operation, setOperation] = useState<OperationState | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadCandidates = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setGroups(await listCrossStateCandidates());
    } catch (cause) {
      setError(errorMessage(cause, "No se pudo cargar la reconciliación."));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void loadCandidates(); }, [loadCandidates]);

  const busy = operation?.phase === "merging";

  const runQueue = useCallback(async (jobs: readonly ReconciliationJob[]) => {
    let applied: readonly AppliedOperation[] = [];
    setOperation({ phase: "merging", jobs, applied, error: null });

    for (const job of jobs) {
      for (const absorbed of job.absorbed) {
        try {
          const result = await mergeGame(absorbed.gameId, job.survivor.gameId);
          if (result.kind !== "applied") {
            setOperation({ phase: "error", jobs, applied, error: result.blockReason ?? "La fusión fue rechazada." });
            return;
          }
          applied = [...applied, { candidateKey: job.candidateKey, absorbed, survivor: job.survivor, result }];
          setOperation({ phase: "merging", jobs, applied, error: null });
        } catch (cause) {
          setOperation({ phase: "error", jobs, applied, error: errorMessage(cause, "No se pudo fusionar el juego.") });
          return;
        }
      }
    }

    setOperation({ phase: "done", jobs, applied, error: null });
    await loadCandidates();
  }, [loadCandidates]);

  function buildJobs(): readonly ReconciliationJob[] {
    return (groups ?? []).flatMap((group) => {
      if (groupBlocked(group) !== null) return [];
      const survivor = group.members.find((member) => member.gameId === selectedSurvivors[group.candidateKey]);
      if (!survivor) return [];
      const absorbedIds = selectedAbsorbed[group.candidateKey] ?? {};
      const absorbed = group.members.filter((member) => member.gameId !== survivor.gameId && absorbedIds[member.gameId] === true);
      return absorbed.length > 0 ? [{ candidateKey: group.candidateKey, survivor, absorbed }] : [];
    });
  }

  function selectSurvivor(candidateKey: string, gameId: number) {
    setSelectedSurvivors((current) => ({ ...current, [candidateKey]: gameId }));
    setSelectedAbsorbed((current) => {
      const existing = current[candidateKey] ?? {};
      const next: Record<number, boolean> = {};
      for (const [id, selected] of Object.entries(existing)) if (Number(id) !== gameId && selected) next[Number(id)] = true;
      return { ...current, [candidateKey]: next };
    });
  }

  function renderOperation() {
    if (!operation) return null;
    const appliedList = operation.applied.length > 0 ? (
      <ul className="space-y-1" aria-label="Operaciones aplicadas">
        {operation.applied.map((item) => (
          <li key={`${item.candidateKey}:${item.absorbed.gameId}`} className="text-xs text-secondary">
            <span className="font-semibold text-primary">{item.absorbed.title}</span> absorbido en{" "}
            <span className="font-semibold text-primary">{item.survivor.title}</span> · {movedSummary(item.result)}
          </li>
        ))}
      </ul>
    ) : null;
    return (
      <section className="app-card space-y-3 p-4" aria-labelledby="reconciliation-operation-heading">
        <p id="reconciliation-operation-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
          {operation.phase === "merging" ? "Fusión en curso" : "Resultado de la fusión"}
        </p>
        {operation.phase === "merging" ? <p className="text-sm text-primary" aria-live="polite">Aplicadas {operation.applied.length} operaciones; quedan {operation.jobs.reduce((count, job) => count + job.absorbed.length, 0) - operation.applied.length} pendientes.</p> : null}
        {operation.phase === "error" ? <Alert variant="danger">{operation.error ?? "No se pudo fusionar el juego."}</Alert> : null}
        {operation.phase === "done" ? <p className="text-sm text-primary" role="status">La fusión terminó y se recargaron los candidatos.</p> : null}
        {appliedList}
        {operation.phase !== "merging" ? <Button type="button" variant="secondary" onClick={() => setOperation(null)}>Cerrar el resultado</Button> : null}
      </section>
    );
  }

  const jobs = buildJobs();
  return (
    <div className="space-y-4">
      <section className="app-card space-y-3 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <span className="tabler-badge tabler-badge-primary">{groups?.length ?? 0} candidatos</span>
          {loading ? <span className="tabler-badge tabler-badge-muted">Cargando</span> : null}
        </div>
        <p className="text-xs text-muted">Selecciona un superviviente y uno o más miembros absorbidos por candidato. Las fusiones requieren confirmación y se ejecutan una por una.</p>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" onClick={() => void loadCandidates()} disabled={loading || busy}>Recargar</Button>
          <Button type="button" variant="danger" disabled={busy || jobs.length === 0} onClick={() => setConfirm(jobs)}>Confirmar fusiones seleccionadas ({jobs.reduce((count, job) => count + job.absorbed.length, 0)})</Button>
        </div>
      </section>

      {error ? <section className="app-card space-y-3 p-4"><Alert variant="danger">{error}</Alert><Button type="button" variant="secondary" onClick={() => void loadCandidates()}>Reintentar</Button></section> : null}
      {renderOperation()}
      {groups?.length === 0 && !loading ? <p className="app-card p-4 text-sm text-muted">No hay candidatos.</p> : null}

      {(groups ?? []).map((group) => {
        const groupReason = groupBlocked(group);
        const survivorId = selectedSurvivors[group.candidateKey] ?? null;
        const absorbed = selectedAbsorbed[group.candidateKey] ?? {};
        return (
          <section key={group.candidateKey} className="app-card space-y-3 p-4" aria-labelledby={`reconciliation-${group.candidateKey}`}>
            <div className="flex flex-wrap gap-2"><h2 id={`reconciliation-${group.candidateKey}`} className="font-semibold text-primary">{group.candidateKey}</h2><span className="tabler-badge tabler-badge-info">{group.confidence}</span>{groupReason ? <span className="tabler-badge tabler-badge-warning">Bloqueado</span> : <span className="tabler-badge tabler-badge-success">Fusionable</span>}</div>
            <p className="text-xs text-secondary">{group.reasons.join(" · ")} {group.warnings.length ? `· ${group.warnings.join(" · ")}` : ""}</p>
            {groupReason ? <p className="text-xs text-secondary"><span className="font-semibold text-primary">Motivo del bloqueo: </span>{groupReason}</p> : null}
            <div className="grid gap-2 md:grid-cols-2">
              {group.members.map((member) => {
                const isSurvivor = survivorId === member.gameId;
                const isAbsorbed = absorbed[member.gameId] === true;
                return <article key={member.gameId} className="rounded border border-default p-3 text-sm">
                  <div className="flex items-start gap-2">
                    <input type="radio" name={`survivor-${group.candidateKey}`} checked={isSurvivor} disabled={busy || groupReason !== null || member.blocked} onChange={() => selectSurvivor(group.candidateKey, member.gameId)} aria-label={`Elegir ${member.title} como superviviente`} />
                    <div className="min-w-0 flex-1"><p className="font-semibold text-primary">{member.title} <span className="text-xs text-muted">gameId {member.gameId}</span></p><p className="text-xs text-secondary">Estados: {member.states.join(", ") || "—"}</p><p className="text-xs text-secondary">Tiendas: {member.storeRows.map((row) => `${row.store}:${row.storeGameId} (${row.state})`).join(", ") || "—"}</p><p className="text-xs text-secondary">Evidencia: {member.evidence.join(", ") || "—"}</p></div>
                  </div>
                  <label className="mt-2 inline-flex items-center gap-2 text-xs text-secondary"><input type="checkbox" checked={isAbsorbed} disabled={busy || groupReason !== null || member.blocked || isSurvivor || survivorId === null} onChange={(event) => setSelectedAbsorbed((current) => ({ ...current, [group.candidateKey]: { ...(current[group.candidateKey] ?? {}), [member.gameId]: event.target.checked } }))} /> Absorber este miembro</label>
                  {member.blocked ? <p className="mt-1 text-[11px] text-secondary"><span className="font-semibold text-primary">Bloqueado: </span>{member.blockReason ?? "El backend no informó el motivo."}</p> : null}
                </article>;
              })}
            </div>
            <p className="text-xs text-muted">{groupReason ? "La acción está deshabilitada por el bloqueo informado." : survivorId === null ? "Elige exactamente un superviviente." : "Marca los miembros que se absorberán."}</p>
          </section>
        );
      })}

      {confirm ? <ConfirmDialog jobs={confirm} busy={busy ?? false} onCancel={() => setConfirm(null)} onConfirm={() => { setConfirm(null); void runQueue(confirm); }} /> : null}
    </div>
  );
}

function ConfirmDialog({ jobs, busy, onCancel, onConfirm }: { readonly jobs: readonly ReconciliationJob[]; readonly busy: boolean; readonly onCancel: () => void; readonly onConfirm: () => void }) {
  return <div className="fixed inset-0 z-50 grid place-items-center bg-black/50 p-4" role="dialog" aria-modal="true" aria-labelledby="reconciliation-confirm-title">
    <div className="app-card max-w-lg space-y-4 p-5"><h2 id="reconciliation-confirm-title" className="text-lg font-semibold text-primary">Confirmar fusiones</h2><p className="text-sm text-secondary">Esta operación es irreversible. Se ejecutarán {jobs.reduce((count, job) => count + job.absorbed.length, 0)} POST secuenciales.</p><ul className="list-disc space-y-1 pl-5 text-sm text-secondary">{jobs.map((job) => <li key={job.candidateKey}>{job.absorbed.map((member) => `${member.title} → ${job.survivor.title}`).join(", ")}</li>)}</ul><div className="flex justify-end gap-2"><Button type="button" variant="secondary" onClick={onCancel} disabled={busy}>Cancelar</Button><Button type="button" variant="danger" onClick={onConfirm} disabled={busy}>Confirmar y ejecutar</Button></div></div>
  </div>;
}
