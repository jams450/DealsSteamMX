"use client";

// Alta de un juego de la wishlist en la biblioteca (L4). El camino es el AppID de Steam de la fila, nunca el
// título: el servidor resuelve la identidad canónica y el diálogo solo manda plataforma, tienda, tipo y la
// fecha opcional. La plataforma y la tienda de adquisición son campos separados porque una clave comprada en
// Amazon se juega en Steam.
//
// S6/L14: además manda dos hechos manuales e independientes, `Tipo de tienda` (oficial/autorizada, keyshop o no
// sé) y `Oferta encontrada en` (ITAD, gg.deals, tienda directa u otro). Se eligen, no se deducen: el proveedor
// donde se vio la oferta no dice si la tienda es oficial, así que ninguna de las dos respuestas se rellena desde
// la otra. La tienda real (`acquiredStore`) se conserva tal cual; clasificarla no la reemplaza.
//
// El descarte de la wishlist es local: el servidor jamás escribe en Steam. El aviso lo dice en voz alta y el
// checkbox es explícito. Viene marcado por omisión (`ACQUIRE_REMOVE_DEFAULT`): quien acaba de comprar quiere
// dejar de verlo en la wishlist, y desmarcarlo es una acción de primera clase que no suprime nada.
//
// Accesibilidad: `aria-modal="true"` obliga a contener el foco. Se usa `nextFocusIndex`, la misma utilidad que
// trapea el drawer del shell (probada junto a la navegación y aquí por regresión), y el efecto de foco inicial
// no depende de `busy`: un submit no vuelve a robar el foco mientras espera la respuesta.
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Library, X } from "lucide-react";
import { nextFocusIndex } from "@/components/navigation/nav-config";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { acquireWishlistItem } from "../_lib/wishlist-api";
import {
  ACQUIRED_STORE_TYPE_OPTIONS,
  ACQUISITION_PROVIDER_OPTIONS,
  ACQUIRE_PLATFORM_OPTIONS,
  ACQUIRE_PLATFORM_OTHER,
  ACQUIRE_PROVIDER_DEFAULT,
  ACQUIRE_PROVIDER_LABEL,
  ACQUIRE_REMOVE_DEFAULT,
  ACQUIRE_STORE_OPTIONS,
  ACQUIRE_STORE_TYPE_DEFAULT,
  ACQUIRE_STORE_TYPE_HINT,
  ACQUIRE_STORE_TYPE_LABEL,
  ACQUISITION_TYPE_OPTIONS,
  STEAM_REMOVAL_NOTICE,
  STEAM_REMOVAL_SYNC_NOTE,
  acquisitionFeedback,
  validateAcquisitionForm,
  type AcquisitionFeedback
} from "../_lib/wishlist-acquire";
import type { WishlistAcquireResult, WishlistItem } from "../_lib/wishlist-contract";

type Props = {
  readonly item: WishlistItem;
  readonly onClose: () => void;
  /** Se dispara con la respuesta del servidor: el padre recarga la lista conservando filtros y página. */
  readonly onAcquired: (result: WishlistAcquireResult) => void;
};

const DEFAULT_STORE = "steam";

// Mismo selector que el drawer del shell (`components/navigation/shell-chrome.tsx`). No se exporta allí, así que
// se repite aquí; lo que sí se comparte es la decisión de a dónde mover el foco (`nextFocusIndex`).
const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

// Prellenar plataforma y tienda con Steam es una comodidad del caso mayoritario (la fila nace de la wishlist
// de Steam); ambas se pueden cambiar y se envían por separado.
export function AcquireDialog({ item, onClose, onAcquired }: Props) {
  const [platform, setPlatform] = useState(DEFAULT_STORE);
  const [customPlatform, setCustomPlatform] = useState("");
  const [acquiredStore, setAcquiredStore] = useState(DEFAULT_STORE);
  const [customAcquiredStore, setCustomAcquiredStore] = useState("");
  const [acquisitionType, setAcquisitionType] = useState<string>("purchase");
  // S6: dos hechos que el usuario declara y que nunca se deducen del proveedor ni del precio. Arrancan en los
  // defaults conservadores (`no sé` + `tienda directa`), que no afirman nada sobre la licencia de la tienda.
  const [acquiredStoreType, setAcquiredStoreType] = useState<string>(ACQUIRE_STORE_TYPE_DEFAULT);
  const [acquisitionProvider, setAcquisitionProvider] = useState<string>(ACQUIRE_PROVIDER_DEFAULT);
  const [acquiredAt, setAcquiredAt] = useState("");
  const [removeFromWishlist, setRemoveFromWishlist] = useState(ACQUIRE_REMOVE_DEFAULT);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<AcquisitionFeedback | null>(null);
  const panelRef = useRef<HTMLElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  // El listener vive una sola vez: `busy` y `onClose` se leen por referencia, así que enviar el formulario no
  // vuelve a montar el efecto ni, por tanto, devuelve el foco al panel en medio de la petición.
  const busyRef = useRef(busy);
  const onCloseRef = useRef(onClose);

  useEffect(() => {
    busyRef.current = busy;
  }, [busy]);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    panelRef.current?.focus();

    function onKeyDown(event: globalThis.KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        if (!busyRef.current) onCloseRef.current();
        return;
      }
      if (event.key !== "Tab" || !panelRef.current) return;

      // Tab/Shift+Tab se contienen en el panel. En medio de la lista devuelve `null` y manda el navegador; en
      // los extremos (y cuando el foco está en el panel o se perdió en el <body>) se envuelve a mano.
      const focusable = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
      const next = nextFocusIndex(
        focusable.indexOf(document.activeElement as HTMLElement),
        focusable.length,
        event.shiftKey
      );
      if (next === null) return;
      event.preventDefault();
      focusable[next]?.focus();
    }

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, []);

  // El formulario desaparece al llegar el feedback: sin esto el foco se caería al <body> y el anuncio sonaría
  // lejos del botón que el usuario acaba de usar.
  useEffect(() => {
    if (feedback !== null) closeButtonRef.current?.focus();
  }, [feedback]);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;

    setError(null);
    const validation = validateAcquisitionForm({
      platform,
      customPlatform,
      acquiredStore,
      customAcquiredStore,
      acquisitionType,
      acquiredAt,
      removeFromWishlist,
      acquiredStoreType,
      acquisitionProvider
    });
    if (!validation.ok) {
      setError(validation.error);
      return;
    }

    setBusy(true);
    try {
      const result = await acquireWishlistItem(item.appId, validation.body);
      setFeedback(acquisitionFeedback(result));
      onAcquired(result);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No se pudo añadir el juego a la biblioteca");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div
      className="fixed inset-0 z-[70] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={() => {
        if (!busy) onClose();
      }}
    >
      <section
        ref={panelRef}
        tabIndex={-1}
        className="app-card max-h-[90dvh] w-full max-w-xl space-y-4 overflow-y-auto p-5 outline-none"
        role="dialog"
        aria-modal="true"
        aria-labelledby="acquire-dialog-title"
        aria-describedby="acquire-dialog-subtitle"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 id="acquire-dialog-title" className="text-lg font-semibold text-primary">
              Pasar a tu biblioteca
            </h2>
            <p id="acquire-dialog-subtitle" className="mt-0.5 text-xs text-muted">
              <span className="font-semibold text-primary">{item.name}</span> · AppID {item.appId}. Se añade como
              posesión de la plataforma que elijas, sin subir ningún JSON.
            </p>
          </div>
          <Button type="button" variant="ghost" onClick={onClose} aria-label="Cerrar" disabled={busy}>
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </Button>
        </div>

        {error ? <Alert variant="danger">{error}</Alert> : null}

        {feedback ? (
          <div className="space-y-3">
            <p
              role="status"
              aria-live="polite"
              className={feedback.tone === "success" ? "text-sm font-semibold text-success" : "text-sm text-primary"}
            >
              {feedback.message}
            </p>
            {removeFromWishlist ? <p className="text-xs text-muted">{STEAM_REMOVAL_NOTICE}</p> : null}
            <div className="flex flex-wrap gap-2">
              <Button ref={closeButtonRef} type="button" variant="primary" onClick={onClose}>
                Cerrar
              </Button>
            </div>
          </div>
        ) : (
          <form className="space-y-4" onSubmit={onSubmit} noValidate>
            <div className="grid gap-3 sm:grid-cols-2">
              <Select
                id="acquire-platform"
                label="Plataforma"
                value={platform}
                disabled={busy}
                onChange={(event) => {
                  setPlatform(event.target.value);
                  setError(null);
                }}
              >
                {ACQUIRE_PLATFORM_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
                <option value={ACQUIRE_PLATFORM_OTHER}>Otra plataforma…</option>
              </Select>

              <Select
                id="acquire-store"
                label="Tienda donde lo adquiriste"
                value={acquiredStore}
                disabled={busy}
                onChange={(event) => {
                  setAcquiredStore(event.target.value);
                  setError(null);
                }}
              >
                {ACQUIRE_STORE_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
                <option value={ACQUIRE_PLATFORM_OTHER}>Otra tienda…</option>
              </Select>
            </div>
            <p className="text-xs text-muted">
              Son campos distintos: la plataforma es dónde lo juegas y la tienda es dónde lo compraste (una clave
              de Amazon para Steam es plataforma Steam, tienda Amazon).
            </p>

            {platform === ACQUIRE_PLATFORM_OTHER ? (
              <div className="space-y-1.5">
                <label htmlFor="acquire-custom-platform" className="text-sm font-medium text-primary">
                  Tu plataforma
                </label>
                <input
                  id="acquire-custom-platform"
                  type="text"
                  value={customPlatform}
                  maxLength={32}
                  onChange={(event) => setCustomPlatform(event.target.value)}
                  placeholder="coleco"
                  autoComplete="off"
                  disabled={busy}
                  className="input-semantic h-10 w-full px-3 text-sm"
                />
                <p className="text-xs text-muted">En minúsculas y sin espacios: letras, números, punto, guion o guion bajo.</p>
              </div>
            ) : null}

            {acquiredStore === ACQUIRE_PLATFORM_OTHER ? (
              <div className="space-y-1.5">
                <label htmlFor="acquire-custom-store" className="text-sm font-medium text-primary">
                  Tu tienda
                </label>
                <input
                  id="acquire-custom-store"
                  type="text"
                  value={customAcquiredStore}
                  maxLength={32}
                  onChange={(event) => setCustomAcquiredStore(event.target.value)}
                  placeholder="tienda_local"
                  autoComplete="off"
                  disabled={busy}
                  className="input-semantic h-10 w-full px-3 text-sm"
                />
                <p className="text-xs text-muted">En minúsculas y sin espacios, con la misma regla que la plataforma.</p>
              </div>
            ) : null}

            <div className="grid gap-3 sm:grid-cols-2">
              <Select
                id="acquire-store-type"
                label={ACQUIRE_STORE_TYPE_LABEL}
                value={acquiredStoreType}
                disabled={busy}
                aria-describedby="acquire-store-type-help"
                onChange={(event) => {
                  setAcquiredStoreType(event.target.value);
                  setError(null);
                }}
              >
                {ACQUIRED_STORE_TYPE_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>

              <Select
                id="acquire-provider"
                label={ACQUIRE_PROVIDER_LABEL}
                value={acquisitionProvider}
                disabled={busy}
                aria-describedby="acquire-store-type-help"
                onChange={(event) => {
                  setAcquisitionProvider(event.target.value);
                  setError(null);
                }}
              >
                {ACQUISITION_PROVIDER_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>
            </div>
            <p id="acquire-store-type-help" className="text-xs text-muted">
              {ACQUIRE_STORE_TYPE_HINT}
            </p>

            <div className="grid gap-3 sm:grid-cols-2">
              <Select
                id="acquire-type"
                label="Tipo de adquisición"
                value={acquisitionType}
                disabled={busy}
                onChange={(event) => {
                  setAcquisitionType(event.target.value);
                  setError(null);
                }}
              >
                {ACQUISITION_TYPE_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>

              <div className="grid gap-1.5 text-sm font-medium text-primary">
                <label htmlFor="acquire-date">Fecha de adquisición (opcional)</label>
                <input
                  id="acquire-date"
                  type="date"
                  value={acquiredAt}
                  onChange={(event) => {
                    setAcquiredAt(event.target.value);
                    setError(null);
                  }}
                  disabled={busy}
                  aria-describedby="acquire-date-help"
                  className="input-semantic h-10 w-full px-3 text-sm"
                />
                <span id="acquire-date-help" className="text-xs font-normal text-muted">
                  Opcional: si no la recuerdas, déjala vacía.
                </span>
              </div>
            </div>
            <p className="text-xs text-muted">
              {ACQUISITION_TYPE_OPTIONS.find((option) => option.value === acquisitionType)?.hint}
            </p>

            <div className="space-y-2 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">
              <label htmlFor="acquire-remove" className="flex items-start gap-2 text-sm text-primary">
                <input
                  id="acquire-remove"
                  type="checkbox"
                  checked={removeFromWishlist}
                  onChange={(event) => setRemoveFromWishlist(event.target.checked)}
                  disabled={busy}
                  aria-describedby="acquire-remove-help"
                  className="mt-0.5 h-4 w-4 shrink-0 accent-[var(--color-accent)]"
                />
                <span>Quitarlo también de mi wishlist local</span>
              </label>
              <div id="acquire-remove-help" className="space-y-1 text-xs text-muted">
                <p>
                  {removeFromWishlist
                    ? STEAM_REMOVAL_SYNC_NOTE
                    : "Desmarcado, el juego sigue en tu wishlist local y no se suprime nada."}
                </p>
                <p>{STEAM_REMOVAL_NOTICE}</p>
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <Button type="submit" variant="primary" loading={busy} loadingText="Añadiendo...">
                <Library className="h-4 w-4" aria-hidden="true" />
                Añadir a la biblioteca
              </Button>
              <span className="text-xs text-muted" aria-live="polite">
                {busy ? "Guardando la adquisición..." : "Sin importes: solo se registra cómo llegó el juego."}
              </span>
            </div>
          </form>
        )}
      </section>
    </div>
  );
}
