"use client";

// Selector de portada de un juego. La fuente la decide el grupo de plataformas, no una casualidad: un
// grupo solo de PC busca en Steam, uno solo de consola busca en IGDB y uno mixto pide que se elija una
// de las dos. Elegir un resultado manda un id — `steamAppId` o `igdbId`, exactamente uno — y el servidor
// resuelve y guarda la URL: este componente jamás envía una imagen. La portada vive en el juego
// canónico, así que es compartida por todas sus copias. Es manual a propósito: la búsqueda por título
// acierta casi siempre, pero no siempre (remakes, ediciones, títulos repetidos), y una portada equivocada
// es visible. Vive fuera de la grilla porque abre encima, no dentro de una celda.
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Search, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/ui/cn";
import { storeLabel } from "@/lib/contracts/stores";
import type { SteamSearchResult } from "@/lib/contracts/steam";
import type { ManualSearchHit } from "@/lib/contracts/manual-library";
import { resolveCoverSource, type CoverSource } from "@/lib/contracts/library-covers";
import { searchSteam } from "@/app/steam/_lib/steam-api";
import { searchManualGames } from "../_lib/manual-library-api";
import { setGameCover } from "../_lib/library-api";

const SOURCE_OPTIONS: readonly { readonly value: CoverSource; readonly label: string }[] = [
  { value: "steam", label: "Steam" },
  { value: "igdb", label: "IGDB" }
];

// Contexto de un resultado de IGDB: año y hasta tres plataformas, para distinguir remakes y ediciones
// que comparten nombre. El título solo no alcanza para elegir bien la fila.
function hitDescription(hit: ManualSearchHit) {
  const platforms = hit.platforms.map((platform) => platform.name).slice(0, 3).join(", ");
  return [hit.releaseYear !== null ? String(hit.releaseYear) : null, platforms || null].filter(Boolean).join(" · ");
}

function copiesLabel(count: number) {
  return count === 1 ? "la copia vinculada" : `las ${count} copias vinculadas`;
}

export function CoverPicker({
  gameId,
  title,
  stores,
  linkedRows,
  onClose,
  onPicked
}: {
  readonly gameId: number;
  readonly title: string;
  /** Tiendas del grupo: deciden la fuente y se enseñan en la nota de la portada compartida. */
  readonly stores: readonly string[];
  /** Copias vinculadas al mismo juego canónico: cuántas filas usan la portada que aquí se elige. */
  readonly linkedRows: number;
  readonly onClose: () => void;
  readonly onPicked: (gameId: number, imageUrl: string) => void;
}) {
  // `null` solo en un grupo mixto: todavía no se eligió catálogo. Un grupo homogéneo lo trae fijado y
  // no se puede cambiar, porque ahí no hay nada que preguntar.
  const lockedSource = resolveCoverSource(stores);
  const [source, setSource] = useState<CoverSource | null>(lockedSource);
  const [query, setQuery] = useState(title);
  // `null` es "todavía no hay resultados" en CADA catálogo: se distingue de "buscó y no encontró nada".
  const [steamResults, setSteamResults] = useState<readonly SteamSearchResult[] | null>(null);
  const [igdbHits, setIgdbHits] = useState<readonly ManualSearchHit[] | null>(null);
  // `source === null` en la respuesta de IGDB es "proveedor no disponible", no "sin resultados".
  const [igdbUnavailable, setIgdbUnavailable] = useState(false);
  const [searching, setSearching] = useState(false);
  // Id guardando, con su catálogo delante: en un grupo mixto Steam 620 e IGDB 620 no son la misma fila.
  const [savingKey, setSavingKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  async function runSearch(term: string, which: CoverSource) {
    const trimmed = term.trim();
    if (trimmed === "") {
      if (which === "steam") setSteamResults([]);
      else setIgdbHits([]);
      return;
    }

    setError(null);
    setSearching(true);
    try {
      if (which === "steam") {
        setSteamResults(await searchSteam(trimmed));
      } else {
        const result = await searchManualGames(trimmed);
        // `source === null` significa «proveedor no disponible», no «sin resultados»: el aviso lo
        // distingue para que la lista vacía no se lea como un título que IGDB no conoce.
        setIgdbUnavailable(result.source === null);
        setIgdbHits(result.hits);
      }
    } catch (cause) {
      const fallback = which === "steam" ? "No se pudo buscar en Steam" : "No se pudo buscar en IGDB";
      // Un fallo deja la lista vacía; el mensaje de "sin resultados" se calla mientras haya error, para
      // no confundir «no pude buscar» con «busqué y no había nada».
      if (which === "steam") setSteamResults([]);
      else setIgdbHits([]);
      setError(cause instanceof Error ? cause.message : fallback);
    } finally {
      setSearching(false);
    }
  }

  // La búsqueda arranca sola con el título en cuanto hay un catálogo elegido: en un grupo homogéneo eso
  // es al abrir el selector; en uno mixto, al pulsar Steam o IGDB. Abrirlo y ver la lista vacía sería un
  // paso de más en el caso normal.
  useEffect(() => {
    if (source === null) return;
    if (source === "steam" && steamResults === null) void runSearch(title, "steam");
    if (source === "igdb" && igdbHits === null) void runSearch(title, "igdb");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [source]);

  useEffect(() => {
    panelRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  async function pick(which: CoverSource, id: number) {
    if (savingKey !== null) return;

    const key = `${which}:${id}`;
    setError(null);
    setSavingKey(key);
    try {
      // Exactamente un id de un catálogo, nunca una URL: el servidor relee la portada y devuelve la que
      // guardó, que es la que la grilla pinta en todas las copias del grupo.
      const imageUrl = await setGameCover(gameId, which === "steam" ? { steamAppId: id } : { igdbId: id });
      onPicked(gameId, imageUrl);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo guardar la portada");
    } finally {
      setSavingKey(null);
    }
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (source === null || searching) return;
    void runSearch(query, source);
  }

  // El encabezado nombra el catálogo en cuanto lo hay: en un grupo mixto todavía no hay nada que nombrar.
  const headingSource = lockedSource ?? source;
  const heading =
    headingSource === "steam" ? "Buscar portada en Steam" : headingSource === "igdb" ? "Buscar portada en IGDB" : "Buscar portada";
  const storesText = stores.map((store) => storeLabel(store)).join(", ");
  const sharedNoteId = "cover-picker-shared-note";

  return (
    <div
      className="fixed inset-0 z-[80] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={onClose}
    >
      <div
        ref={panelRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="cover-picker-title"
        aria-describedby={sharedNoteId}
        className="app-card flex max-h-[85dvh] w-full max-w-2xl flex-col overflow-hidden outline-none"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3 border-b border-default p-4">
          <div className="min-w-0">
            <h2 id="cover-picker-title" className="text-lg font-semibold text-primary">
              {heading}
            </h2>
            <p className="mt-0.5 truncate text-xs text-muted">{title}</p>
          </div>
          <Button type="button" variant="ghost" className="h-9 w-9 p-0" aria-label="Cerrar" onClick={onClose}>
            <X className="h-4 w-4" aria-hidden="true" />
          </Button>
        </div>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
          {/* Solo un grupo mixto pregunta: PC y consola conviven en el mismo juego y la portada es una
              sola, así que la fuente no se elige sola. */}
          {lockedSource === null ? (
            <div className="space-y-1.5">
              <p id="cover-picker-source-label" className="text-sm font-medium text-primary">
                ¿En qué catálogo buscas la portada?
              </p>
              <div
                className="flex flex-wrap items-center gap-1 border border-strong bg-[var(--color-surface-2)] p-0.5"
                role="group"
                aria-labelledby="cover-picker-source-label"
              >
                {SOURCE_OPTIONS.map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    aria-pressed={source === option.value}
                    onClick={() => {
                      setSource(option.value);
                      setError(null);
                    }}
                    className={cn(
                      "h-9 px-3 text-xs font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                      source === option.value
                        ? "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]"
                        : "text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary"
                    )}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
              <p className="text-xs text-muted">
                Este juego está en tiendas de PC y en consola, y la portada es una sola para todas sus
                copias: elige dónde buscarla.
              </p>
            </div>
          ) : null}

          {error ? <Alert variant="danger">{error}</Alert> : null}
          {source === "igdb" && igdbUnavailable ? (
            <Alert variant="info">
              La búsqueda no está disponible (IGDB sin configurar o con error). Prueba más tarde o con otro
              título: el catálogo de Steam sigue pudiendo dar con la portada.
            </Alert>
          ) : null}

          {source !== null ? (
            <form className="flex items-end gap-2" onSubmit={onSubmit}>
              <div className="flex-1">
                <Input
                  id="cover-picker-query"
                  label={source === "steam" ? "Título en Steam" : "Título a buscar"}
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  autoComplete="off"
                />
              </div>
              <Button type="submit" variant="secondary" className="h-10 px-3 text-xs" loading={searching}>
                <Search className="h-3.5 w-3.5" aria-hidden="true" />
                Buscar
              </Button>
            </form>
          ) : null}

          {source === "steam" ? (
            steamResults === null ? (
              <p className="text-sm text-muted">Buscando en Steam...</p>
            ) : steamResults.length === 0 ? (
              error === null ? (
                <p className="text-sm text-muted">Steam no devolvió resultados para esa búsqueda.</p>
              ) : null
            ) : (
              <ul className="space-y-2">
                {steamResults.map((result) => (
                  <li
                    key={result.appId}
                    className="flex items-center gap-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3"
                  >
                    <span className="inline-flex h-[45px] w-[120px] shrink-0 items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface)]">
                      {result.imageUrl ? (
                        <img
                          src={result.imageUrl}
                          alt=""
                          width={120}
                          height={45}
                          loading="lazy"
                          decoding="async"
                          className="h-full w-full object-cover"
                        />
                      ) : (
                        <span className="text-[11px] text-muted">Sin imagen</span>
                      )}
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-sm font-semibold text-primary">{result.name}</p>
                      <p className="text-xs text-muted">
                        AppID {result.appId}
                        {result.type ? ` · ${result.type}` : ""}
                      </p>
                    </div>
                    <Button
                      type="button"
                      variant="primary"
                      className="h-9 whitespace-nowrap px-3 text-xs"
                      loading={savingKey === `steam:${result.appId}`}
                      onClick={() => void pick("steam", result.appId)}
                    >
                      Usar esta
                    </Button>
                  </li>
                ))}
              </ul>
            )
          ) : source === "igdb" ? (
            igdbUnavailable ? null : igdbHits === null ? (
              <p className="text-sm text-muted">Buscando en IGDB...</p>
            ) : igdbHits.length === 0 ? (
              error === null ? (
                <p className="text-sm text-muted">IGDB no devolvió resultados para esa búsqueda.</p>
              ) : null
            ) : (
              <ul className="space-y-2">
                {igdbHits.map((hit) => {
                  const description = hitDescription(hit);
                  return (
                    <li
                      key={hit.igdbId}
                      className="flex items-center gap-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3"
                    >
                      <span className="inline-flex h-[72px] w-14 shrink-0 items-center justify-center overflow-hidden rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface)]">
                        {hit.imageUrl ? (
                          // eslint-disable-next-line @next/next/no-img-element -- URL de IGDB servida por el backend, no una ruta interna
                          <img
                            src={hit.imageUrl}
                            alt=""
                            width={56}
                            height={72}
                            loading="lazy"
                            decoding="async"
                            className="h-full w-full object-cover"
                          />
                        ) : (
                          <span className="text-[11px] text-muted">Sin imagen</span>
                        )}
                      </span>
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-semibold text-primary">{hit.title}</p>
                        <p className="truncate text-xs text-muted">
                          IGDB {hit.igdbId}
                          {description ? ` · ${description}` : ""}
                        </p>
                      </div>
                      <Button
                        type="button"
                        variant="primary"
                        className="h-9 whitespace-nowrap px-3 text-xs"
                        loading={savingKey === `igdb:${hit.igdbId}`}
                        onClick={() => void pick("igdb", hit.igdbId)}
                      >
                        Usar esta
                      </Button>
                    </li>
                  );
                })}
              </ul>
            )
          ) : (
            <p className="text-sm text-muted">Elige Steam o IGDB para empezar a buscar.</p>
          )}

          <p id={sharedNoteId} className="text-xs text-muted">
            La portada es del juego del catálogo y es compartida: al guardarla se aplica a{" "}
            {copiesLabel(linkedRows)}
            {storesText ? ` (${storesText})` : ""} y reemplaza la que hubiera. El navegador solo manda el id
            elegido — Steam o IGDB —; la URL la resuelve y guarda el servidor, nunca se envía una imagen.
          </p>
        </div>
      </div>
    </div>
  );
}
