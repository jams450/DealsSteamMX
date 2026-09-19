"use client";

// Herramienta de mantenimiento: grupos de juegos canónicos que apuntan al mismo título y la fusión
// manual entre ellos. Es una operación IRREVERSIBLE, así que el flujo tiene pasos explícitos: elegir el
// superviviente (radio dentro del grupo) y confirmar en un diálogo con los títulos reales. Las reseñas
// nunca se pierden: todas pasan al superviviente.
import { useCallback, useEffect, useRef, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { storeLabel } from "@/lib/contracts/stores";
import { type DuplicateGroup, type DuplicateMember, type MergeApplied } from "@/lib/contracts/games-merge";
import { listDuplicateGroups, mergeGame } from "./_lib/games-merge-api";

function errorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error ? cause.message : fallback;
}

// Motivo por el que un grupo no ofrece la fusión. El contrato solo trae `blocked` (sin texto) en el
// grupo; el motivo se lee del primer miembro bloqueado.
function groupBlockReason(group: DuplicateGroup): string | null {
  const blocked = group.members.find((member) => member.blocked);
  if (blocked) return `${blocked.title}: ${blocked.blockReason ?? "miembro bloqueado"}`;
  return group.blocked ? "El grupo está bloqueado." : null;
}

function StoreChips({ member }: { readonly member: DuplicateMember }) {
  if (member.stores.length === 0) return <span className="text-xs text-muted">—</span>;

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {member.stores.map((ref) => (
        <span
          key={`${ref.store}:${ref.storeGameId}`}
          className="inline-flex items-center gap-1.5 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] px-2 py-0.5 text-[11px]"
        >
          <span className="font-semibold text-primary">{storeLabel(ref.store)}</span>
          <span className="tabular-nums text-muted">{ref.storeGameId}</span>
        </span>
      ))}
    </div>
  );
}

function SteamAppIds({ member }: { readonly member: DuplicateMember }) {
  if (member.steamAppIds.length === 0) return <span className="text-xs text-muted">—</span>;

  return (
    <div className="flex flex-wrap items-center gap-1">
      {member.steamAppIds.map((appId) => (
        <span key={appId} className="tabler-badge tabler-badge-info">
          AppID {appId}
        </span>
      ))}
    </div>
  );
}

type AppliedMerge = {
  readonly absorbed: DuplicateMember;
  readonly survivorTitle: string;
  readonly movedExternalIds: number | null;
  readonly movedSteamGames: number | null;
  readonly movedLibraryRows: number | null;
};

// Estado del flujo de fusión. Vive en el cliente porque la fusión es una secuencia de peticiones (una por
// juego absorbido).
type MergeOperation = {
  readonly phase: "merging" | "done" | "error";
  readonly survivor: DuplicateMember;
  readonly applied: readonly AppliedMerge[];
  readonly error: string | null;
};

function toApplied(member: DuplicateMember, survivor: DuplicateMember, result: MergeApplied): AppliedMerge {
  return {
    absorbed: member,
    survivorTitle: survivor.title,
    movedExternalIds: result.movedExternalIds,
    movedSteamGames: result.movedSteamGames,
    movedLibraryRows: result.movedLibraryRows
  };
}

function movedSummary(item: AppliedMerge): string {
  const parts: string[] = [];
  if (item.movedExternalIds !== null) parts.push(`${item.movedExternalIds} identificadores`);
  if (item.movedSteamGames !== null) parts.push(`${item.movedSteamGames} filas de Steam`);
  if (item.movedLibraryRows !== null) parts.push(`${item.movedLibraryRows} filas de biblioteca`);
  return parts.length > 0 ? parts.join(" · ") : "sin contadores informados";
}

type ConfirmState = {
  readonly survivor: DuplicateMember;
  readonly absorbed: readonly DuplicateMember[];
};

export function DuplicatesClient() {
  const [groups, setGroups] = useState<readonly DuplicateGroup[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Superviviente elegido por grupo, indexado por posición de la lista (los grupos no traen id propio).
  const [survivors, setSurvivors] = useState<Record<number, number>>({});
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [op, setOp] = useState<MergeOperation | null>(null);

  const loadGroups = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      setGroups(await listDuplicateGroups());
    } catch (cause) {
      setLoadError(errorMessage(cause, "No se pudieron cargar los duplicados."));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadGroups();
  }, [loadGroups]);

  const busy = op?.phase === "merging";

  // Secuencia de fusiones: un POST por juego absorbido. El único 409 posible es la identidad de Steam
  // ambigua, que ya deshabilita el grupo: si igual llega, se muestra como error y no se sigue.
  const runQueue = useCallback(
    async (
      survivor: DuplicateMember,
      queue: readonly DuplicateMember[],
      appliedSoFar: readonly AppliedMerge[]
    ) => {
      let applied = appliedSoFar;

      for (let index = 0; index < queue.length; index += 1) {
        const absorbed = queue[index];
        try {
          const result = await mergeGame(absorbed.gameId, survivor.gameId);

          if (result.kind === "applied") {
            applied = [...applied, toApplied(absorbed, survivor, result)];
            continue;
          }

          setOp({
            phase: "error",
            survivor,
            applied,
            error: result.blockReason ?? "La fusión fue rechazada."
          });
          return;
        } catch (cause) {
          setOp({
            phase: "error",
            survivor,
            applied,
            error: errorMessage(cause, "No se pudo fusionar el juego.")
          });
          return;
        }
      }

      setOp({ phase: "done", survivor, applied, error: null });
      await loadGroups();
    },
    [loadGroups]
  );

  async function startMerge(state: ConfirmState) {
    setConfirm(null);
    // Los grupos cambian con una fusión, así que al terminar la lista se recarga: la selección se limpia.
    setSurvivors({});
    setOp({
      phase: "merging",
      survivor: state.survivor,
      applied: [],
      error: null
    });
    await runQueue(state.survivor, state.absorbed, []);
  }

  const groupCount = groups?.length ?? 0;

  function renderOpPanel() {
    if (op === null) return null;

    const appliedList =
      op.applied.length > 0 ? (
        <ul className="space-y-1">
          {op.applied.map((item) => (
            <li key={item.absorbed.gameId} className="text-xs text-secondary">
              <span className="font-semibold text-primary">{item.absorbed.title}</span> absorbido en{" "}
              <span className="font-semibold text-primary">{item.survivorTitle}</span> · {movedSummary(item)}
            </li>
          ))}
        </ul>
      ) : null;

    if (op.phase === "merging") {
      return (
        <section className="app-card p-4" aria-labelledby="duplicates-op-heading">
          <p id="duplicates-op-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
            Fusión en curso
          </p>
          <p className="mt-1 text-sm text-primary" aria-live="polite">
            Fusionando en «{op.survivor.title}»...
          </p>
        </section>
      );
    }

    if (op.phase === "error") {
      return (
        <section className="app-card space-y-3 p-4" aria-labelledby="duplicates-op-heading">
          <p id="duplicates-op-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
            Resultado de la fusión
          </p>
          <Alert variant="danger">{op.error ?? "No se pudo fusionar el juego."}</Alert>
          {appliedList}
          <Button type="button" variant="secondary" onClick={() => setOp(null)}>
            Cerrar el resultado
          </Button>
        </section>
      );
    }

    if (op.phase === "done") {
      return (
        <section className="app-card space-y-3 p-4" aria-labelledby="duplicates-op-heading">
          <p id="duplicates-op-heading" className="text-xs font-semibold uppercase tracking-widest text-muted">
            Resultado de la fusión
          </p>
          <div className="flex flex-wrap items-center gap-2">
            <span className="tabler-badge tabler-badge-success">{op.applied.length} absorbidos</span>
            <span className="tabler-badge tabler-badge-info">Superviviente {op.survivor.title}</span>
          </div>
          <p className="text-sm text-primary" role="status">
            La fusión terminó. Los juegos absorbidos ya no existen en el catálogo.
          </p>
          {appliedList}
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="secondary" onClick={() => setOp(null)}>
              Cerrar el resultado
            </Button>
            <Button type="button" variant="secondary" onClick={() => void loadGroups()}>
              Recargar duplicados
            </Button>
          </div>
        </section>
      );
    }

    return null;
  }

  return (
    <div className="space-y-4">
      <section className="app-card space-y-3 p-4" aria-labelledby="duplicates-heading">
        <div className="space-y-1">
          <p className="text-xs font-semibold uppercase tracking-widest text-muted">Mantenimiento</p>
          <h2 id="duplicates-heading" className="text-xl font-semibold tracking-tight text-primary">
            Grupos duplicados
          </h2>
          <div className="flex flex-wrap items-center gap-2">
            <span className="tabler-badge tabler-badge-primary">
              {groupCount === 1 ? "1 grupo" : `${groupCount} grupos`}
            </span>
            {loading ? <span className="tabler-badge tabler-badge-muted">Cargando</span> : null}
          </div>
          <p className="text-xs text-muted">
            Cada grupo reúne juegos canónicos distintos que corresponden al mismo título. Elegir un
            superviviente y fusionar es irreversible: el juego absorbido se borra del catálogo y sus
            identificadores, filas de Steam, filas de biblioteca y reseñas pasan al que sobrevive. Nada se
            descarta: si los dos juegos tienen reseña en la misma tienda, el superviviente queda con las
            dos.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" variant="secondary" onClick={() => void loadGroups()} disabled={loading}>
            Recargar
          </Button>
        </div>
      </section>

      {loadError ? (
        <section className="app-card space-y-3 p-4">
          <Alert variant="danger">{loadError}</Alert>
          <Button type="button" variant="secondary" onClick={() => void loadGroups()}>
            Reintentar
          </Button>
        </section>
      ) : null}

      {renderOpPanel()}

      {loading && groups === null ? (
        <p className="app-card p-4 text-sm text-muted">Cargando...</p>
      ) : !loadError && groupCount === 0 ? (
        <p className="app-card p-4 text-sm text-muted">
          No hay grupos de duplicados pendientes. El catálogo no tiene juegos canónicos que compartan título
          normalizado con identidades distintas.
        </p>
      ) : null}

      {(groups ?? []).map((group, index) => {
        const blockReason = groupBlockReason(group);
        const survivorId = survivors[index] ?? null;
        const survivor = survivorId === null ? null : (group.members.find((member) => member.gameId === survivorId) ?? null);
        const absorbed = survivor === null ? [] : group.members.filter((member) => member.gameId !== survivor.gameId);
        const canMerge = blockReason === null && survivor !== null && absorbed.length > 0 && !busy;

        return (
          <section key={`${index}:${group.foldedTitle}`} className="app-card space-y-3 p-4" aria-labelledby={`duplicate-group-${index}`}>
            <div className="flex flex-wrap items-center gap-2">
              <h3 id={`duplicate-group-${index}`} className="text-base font-semibold tracking-tight text-primary">
                {group.foldedTitle}
              </h3>
              <span className="tabler-badge tabler-badge-muted">
                {group.members.length === 1 ? "1 miembro" : `${group.members.length} miembros`}
              </span>
              {blockReason === null ? (
                <span className="tabler-badge tabler-badge-success">Fusionable</span>
              ) : (
                <span className="tabler-badge tabler-badge-warning">Bloqueado</span>
              )}
            </div>

            {blockReason !== null ? (
              <p className="text-xs text-secondary">
                <span className="font-semibold text-primary">Motivo del bloqueo: </span>
                {blockReason}
              </p>
            ) : null}

            <div className="table-shell max-w-full overflow-x-auto overscroll-x-contain rounded-xl">
              <table className="w-full min-w-[48rem] text-left">
                <caption className="sr-only">
                  Miembros del grupo {group.foldedTitle}. Elige con la primera columna el juego que sobrevive.
                </caption>
                <thead className="table-head">
                  <tr>
                    <th scope="col" className="px-2 py-2 text-xs font-semibold text-primary">
                      Superviviente
                    </th>
                    <th scope="col" className="px-2 py-2 text-xs font-semibold text-primary">
                      Juego
                    </th>
                    <th scope="col" className="px-2 py-2 text-xs font-semibold text-primary">
                      Tiendas
                    </th>
                    <th scope="col" className="px-2 py-2 text-xs font-semibold text-primary">
                      AppID Steam
                    </th>
                    <th scope="col" className="px-2 py-2 text-xs font-semibold text-primary">
                      Estado
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {group.members.map((member) => {
                    const selectable = blockReason === null;
                    return (
                      <tr key={member.gameId} className="table-row transition">
                        <td className="table-cell px-2 py-2">
                          <input
                            id={`survivor-${index}-${member.gameId}`}
                            type="radio"
                            name={`survivor-${index}`}
                            value={member.gameId}
                            checked={survivorId === member.gameId}
                            disabled={!selectable || busy}
                            onChange={() => setSurvivors((current) => ({ ...current, [index]: member.gameId }))}
                            aria-label={`Elegir «${member.title}» como el juego que sobrevive`}
                            className="h-4 w-4 accent-[var(--color-accent)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                          />
                        </td>
                        <td className="table-cell px-2 py-2 text-xs">
                          <label htmlFor={`survivor-${index}-${member.gameId}`} className="font-semibold text-primary">
                            {member.title}
                          </label>
                          <span className="ml-2 tabular-nums text-muted">gameId {member.gameId}</span>
                        </td>
                        <td className="table-cell px-2 py-2">
                          <StoreChips member={member} />
                        </td>
                        <td className="table-cell px-2 py-2">
                          <SteamAppIds member={member} />
                        </td>
                        <td className="table-cell px-2 py-2">
                          {member.blocked ? (
                            <div className="space-y-1">
                              <span className="tabler-badge tabler-badge-warning">Bloqueado</span>
                              <p className="text-[11px] text-secondary">
                                {member.blockReason ?? "El backend no informó el motivo."}
                              </p>
                            </div>
                          ) : (
                            <span className="tabler-badge tabler-badge-neutral">Disponible</span>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <Button
                type="button"
                variant="danger"
                disabled={!canMerge}
                onClick={() => {
                  if (survivor === null || absorbed.length === 0) return;
                  setConfirm({ survivor, absorbed });
                }}
              >
                {survivor === null
                  ? "Elige el juego que sobrevive"
                  : absorbed.length === 1
                    ? `Fusionar 1 juego en «${survivor.title}»`
                    : `Fusionar ${absorbed.length} juegos en «${survivor.title}»`}
              </Button>
              <span className="text-xs text-muted">
                {blockReason !== null
                  ? "La fusión está deshabilitada mientras el grupo o un miembro esté bloqueado."
                  : survivor === null
                    ? "Elige con la primera columna el juego que sobrevive; los demás se absorben y desaparecen."
                    : `Se absorberán: ${absorbed.map((member) => member.title).join(", ")}.`}
              </span>
            </div>
          </section>
        );
      })}

      {confirm !== null ? (
        <MergeConfirmDialog
          survivor={confirm.survivor}
          absorbed={confirm.absorbed}
          busy={busy}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void startMerge(confirm)}
        />
      ) : null}
    </div>
  );
}

type MergeConfirmDialogProps = {
  readonly survivor: DuplicateMember;
  readonly absorbed: readonly DuplicateMember[];
  readonly busy: boolean;
  readonly onCancel: () => void;
  readonly onConfirm: () => void;
};

// Confirmación destructiva: títulos reales, la lista de absorbidos y el aviso de irreversibilidad. Es un
// `alertdialog` nativo con Escape para cancelar y foco inicial en el botón de confirmar, así que se opera
// completo con teclado y el estado se anuncia por `alertdialog` + `aria-describedby`.
function MergeConfirmDialog({ survivor, absorbed, busy, onCancel, onConfirm }: MergeConfirmDialogProps) {
  const confirmRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    confirmRef.current?.focus();

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onCancel();
    }

    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      previouslyFocused?.focus();
    };
  }, [onCancel]);

  return (
    <div
      className="fixed inset-0 z-[100] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={onCancel}
    >
      <section
        className="app-card w-full max-w-lg space-y-3 p-4"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="merge-confirm-title"
        aria-describedby="merge-confirm-desc"
        onClick={(event) => event.stopPropagation()}
      >
        <h2 id="merge-confirm-title" className="text-base font-semibold text-primary">
          ¿Fusionar estos juegos?
        </h2>
        <div id="merge-confirm-desc" className="space-y-2 text-sm text-secondary">
          <p>
            Sobrevive <span className="font-semibold text-primary">{survivor.title}</span> (gameId{" "}
            <span className="tabular-nums">{survivor.gameId}</span>).
          </p>
          <p>Estos juegos se absorben y se borran del catálogo:</p>
          <ul className="list-disc space-y-1 pl-5">
            {absorbed.map((member) => (
              <li key={member.gameId}>
                <span className="font-semibold text-primary">{member.title}</span>{" "}
                <span className="tabular-nums text-muted">gameId {member.gameId}</span>
              </li>
            ))}
          </ul>
          <p>La fusión no se puede deshacer.</p>
        </div>

        <div className="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="secondary" disabled={busy} onClick={onCancel}>
            Cancelar
          </Button>
          <Button ref={confirmRef} type="button" variant="danger" loading={busy} loadingText="Fusionando..." onClick={onConfirm}>
            Sí, fusionar
          </Button>
        </div>
      </section>
    </div>
  );
}
