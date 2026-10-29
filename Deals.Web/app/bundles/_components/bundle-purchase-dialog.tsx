"use client";

// Diálogo de compra de un bundle (S3-S5). Se abre desde la tarjeta del catálogo y no escribe nada hasta
// que el usuario confirma: primero pide la vista previa (snapshot) y muestra qué se va a importar.
//
// Reglas que este componente hace visibles:
// - Tier fijo: la membresía elegible del servidor es la selección; no se desmarca nada.
// - Tier de selección: exactamente N juegos elegidos entre los elegibles (el cupo lo publica el servidor).
// - Un ítem sin identidad no se confirma sin vincularlo a una ficha del catálogo; un ítem identificado por
//   ITAD nunca manda un id explícito.
// - Plataforma por tier con override por juego; fecha, tienda y proveedor son hechos que el usuario
//   declara, no deducciones.
// - Nunca se muestran ni se piden códigos de activación: el contrato registra juegos.
//
// Robustez: un solo envío a la vez (ref + estado, porque el doble clic ocurre antes de que React
// re-renderice); un 400 del servidor significa «snapshot viejo» y se responde volviendo a pedir la vista
// previa, no reintentando a ciegas; 404 y 409 se explican con su reintento. Un cambio de plataforma
// recarga la vista previa sin perder los clics del usuario (la selección se conserva por itemKey).
import { useEffect, useRef, useState, type FormEvent } from "react";
import { ExternalLink, Link2, X } from "lucide-react";
import { nextFocusIndex } from "@/components/navigation/nav-config";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/bff/client-session";
import { formatCurrency } from "@/lib/format/currency";
import { STORE_KEYS, storeLabel } from "@/lib/contracts/stores";
import type {
  BundlePurchasePreview,
  BundlePurchasePreviewItem,
  BundlePurchasePreviewTier,
  CatalogCandidate
} from "@/lib/contracts/bundle-purchases";
import {
  ACQUIRED_STORE_TYPE_DEFAULT,
  ACQUIRED_STORE_TYPE_OPTIONS,
  ACQUISITION_PROVIDER_OPTIONS,
  PURCHASE_PLATFORM_DEFAULT,
  PURCHASE_PLATFORM_OPTIONS,
  TIER_BLOCKED_REASON_LABELS,
  acquisitionDateDefault,
  defaultAcquiredStore,
  defaultProviderForSource,
  defaultTierKey,
  eligibilityLabel,
  initialSelection,
  outcomeLabel,
  purchaseFeedback,
  selectionStatus,
  toggleSelection,
  unlinkedItems,
  validatePurchaseConfirm,
  type ManualLink,
  type PurchaseFeedback
} from "../_lib/bundle-purchase";
import { createBundlePurchase, getBundlePurchasePreview } from "../_lib/bundle-purchases-api";
import { BundlePurchaseLinkSearch } from "./bundle-purchase-link-search";

export type BundlePurchaseTarget = {
  readonly source: string;
  readonly bundleKey: string;
  readonly title: string;
};

type Props = {
  readonly target: BundlePurchaseTarget;
  readonly onClose: () => void;
  /** La compra quedó registrada (o el servidor ya la tenía): el padre refresca catálogo e historial. */
  readonly onPurchased: () => void;
};

// Marca local del <select> de tienda que abre el campo libre. Nunca viaja al servidor.
const STORE_OTHER = "other";

const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

const observedFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "medium" });

function formatObserved(value: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : observedFormatter.format(date);
}

function formatMinor(amountMinor: number | null, currency: string | null): string | null {
  if (amountMinor === null || currency === null) return null;
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

function tierModeText(tier: BundlePurchasePreviewTier): string {
  if (tier.selectionMode === "choice") {
    return tier.requiredSelectionCount === null
      ? "Selección de juegos"
      : `Elige exactamente ${tier.requiredSelectionCount} de los juegos elegibles`;
  }
  return "Se importan automáticamente sus juegos elegibles";
}

function ItemRow({
  item,
  tier,
  willImport,
  atLimit,
  busy,
  platform,
  platformOverride,
  link,
  linkOpen,
  onToggle,
  onPlatformChange,
  onOpenLink,
  onClearLink,
  onPickLink,
  onCancelLink
}: {
  readonly item: BundlePurchasePreviewItem;
  readonly tier: BundlePurchasePreviewTier;
  readonly willImport: boolean;
  readonly atLimit: boolean;
  readonly busy: boolean;
  readonly platform: string;
  readonly platformOverride: string | undefined;
  readonly link: ManualLink | undefined;
  readonly linkOpen: boolean;
  readonly onToggle: () => void;
  readonly onPlatformChange: (value: string) => void;
  readonly onOpenLink: () => void;
  readonly onClearLink: () => void;
  readonly onPickLink: (candidate: CatalogCandidate) => void;
  readonly onCancelLink: () => void;
}) {
  const checkboxDisabled = busy || !item.eligible || (!willImport && atLimit);

  return (
    <li className="space-y-2 rounded-[var(--radius-md)] border border-default p-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 space-y-1">
          {tier.selectionMode === "choice" && item.eligible ? (
            <label htmlFor={`bundle-item-${item.itemKey}`} className="flex items-start gap-2 text-sm text-primary">
              <input
                id={`bundle-item-${item.itemKey}`}
                type="checkbox"
                checked={willImport}
                disabled={checkboxDisabled}
                onChange={onToggle}
                aria-describedby={`bundle-item-help-${item.itemKey}`}
                className="mt-0.5 h-4 w-4 shrink-0 accent-[var(--color-accent)]"
              />
              <span className="font-semibold">{item.title}</span>
            </label>
          ) : (
            <p className="text-sm font-semibold text-primary">{item.title}</p>
          )}
          <p className="flex flex-wrap items-center gap-1.5 text-xs text-muted">
            <span className={item.eligible ? "tabler-badge tabler-badge-success" : "tabler-badge tabler-badge-muted"}>
              {eligibilityLabel(item)}
            </span>
            {item.owned === true ? (
              <span className="tabler-badge tabler-badge-warning">
                Ya lo tienes{item.ownedPlatform ? ` en ${storeLabel(item.ownedPlatform)}` : ""}: quedará como key duplicada
              </span>
            ) : item.owned === false ? (
              <span className="tabler-badge tabler-badge-muted">No está en tu biblioteca</span>
            ) : null}
            {item.requiresManualLink ? (
              <span className="tabler-badge tabler-badge-info">Sin identidad: requiere vínculo</span>
            ) : null}
          </p>
          {tier.selectionMode === "choice" && !item.eligible ? (
            <p id={`bundle-item-help-${item.itemKey}`} className="text-xs text-muted">
              No es un juego: no entra en la importación ni en el cupo de selección.
            </p>
          ) : null}
          {tier.selectionMode === "choice" && item.eligible && atLimit && !willImport ? (
            <p id={`bundle-item-help-${item.itemKey}`} className="text-xs text-muted">
              Ya elegiste el cupo exacto de este tier: desmarca otro juego para cambiarlo por este.
            </p>
          ) : null}
        </div>
        {item.url ? (
          <a
            href={item.url}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 text-xs text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            Ver en la tienda
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
            <span className="sr-only">(se abre en una pestaña nueva)</span>
          </a>
        ) : null}
      </div>

      {willImport ? (
        <div className="space-y-2">
          <Select
            id={`bundle-item-platform-${item.itemKey}`}
            label={`Plataforma de «${item.title}»`}
            value={platformOverride ?? ""}
            disabled={busy}
            onChange={(event) => onPlatformChange(event.target.value)}
          >
            <option value="">Igual que la compra ({storeLabel(platform)})</option>
            {PURCHASE_PLATFORM_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>

          {item.requiresManualLink ? (
            link !== undefined ? (
              <p className="flex flex-wrap items-center gap-2 text-xs text-secondary">
                <span className="tabler-badge tabler-badge-success">Vinculado</span>
                <span className="font-semibold text-primary">{link.title}</span>
                <span className="text-muted">(ficha #{link.gameId})</span>
                <Button type="button" variant="ghost" disabled={busy} onClick={onOpenLink}>
                  Cambiar vínculo
                </Button>
                <Button type="button" variant="ghost" disabled={busy} onClick={onClearLink}>
                  Quitar vínculo
                </Button>
              </p>
            ) : (
              <Button type="button" variant="secondary" disabled={busy} onClick={onOpenLink}>
                <Link2 className="h-4 w-4" aria-hidden="true" />
                Vincular con el catálogo
              </Button>
            )
          ) : null}

          {linkOpen ? (
            <BundlePurchaseLinkSearch
              key={item.itemKey}
              itemTitle={item.title}
              onPick={onPickLink}
              onCancel={onCancelLink}
            />
          ) : null}
        </div>
      ) : null}
    </li>
  );
}

export function BundlePurchaseDialog({ target, onClose, onPurchased }: Props) {
  const [preview, setPreview] = useState<BundlePurchasePreview | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [staleNotice, setStaleNotice] = useState<string | null>(null);
  const [tierKey, setTierKey] = useState<string | null>(null);
  const [selected, setSelected] = useState<readonly string[]>([]);
  const [itemPlatforms, setItemPlatforms] = useState<Readonly<Record<string, string>>>({});
  const [manualLinks, setManualLinks] = useState<Readonly<Record<string, ManualLink>>>({});
  const [linkTarget, setLinkTarget] = useState<string | null>(null);
  const [platform, setPlatform] = useState(PURCHASE_PLATFORM_DEFAULT);
  const [storeChoice, setStoreChoice] = useState("");
  const [customStore, setCustomStore] = useState("");
  const [storeType, setStoreType] = useState<string>(ACQUIRED_STORE_TYPE_DEFAULT);
  const [provider, setProvider] = useState<string>(() => defaultProviderForSource(target.source));
  // `new Date()` dentro del inicializador: el diálogo solo se monta después de un clic en el cliente, así
  // que no hay HTML de servidor con el que discrepar. La fecha es editable y nunca se manda inventada.
  const [acquiredAt, setAcquiredAt] = useState<string>(() => acquisitionDateDefault(new Date()));
  const [busy, setBusy] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<PurchaseFeedback | null>(null);
  const [feedbackDetails, setFeedbackDetails] = useState<readonly string[]>([]);
  const [resultGames, setResultGames] = useState<
    readonly { itemKey: string; title: string; platform: string | null; outcome: string | null; wishlistRemoved: boolean }[]
  >([]);

  const panelRef = useRef<HTMLElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const busyRef = useRef(false);
  // Espejos síncronos de la selección: una recarga de la vista previa (u otra respuesta en vuelo) no puede
  // pisar lo que el usuario acaba de marcar, porque la intención se lee de estos refs al aplicar la respuesta.
  const tierKeyRef = useRef<string | null>(null);
  const selectedRef = useRef<readonly string[]>([]);
  const onCloseRef = useRef(onClose);
  // Secuencia de la vista previa: solo la última respuesta puede tocar el estado.
  const previewSeq = useRef(0);
  const previewAbort = useRef<AbortController | null>(null);
  const storeInitialized = useRef(false);
  const purchasedNotified = useRef(false);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  const activeTier = preview?.tiers.find((tier) => tier.tierKey === tierKey) ?? null;
  const importedKeys =
    activeTier === null
      ? []
      : activeTier.selectionMode === "fixed"
        ? activeTier.items.filter((item) => item.eligible).map((item) => item.itemKey)
        : selected;
  const status = selectionStatus(activeTier, selected);
  const atLimit =
    activeTier?.selectionMode === "choice" &&
    activeTier.requiredSelectionCount !== null &&
    selected.length >= activeTier.requiredSelectionCount;
  const pendingLinks = unlinkedItems(activeTier, importedKeys, manualLinks);

  const derivedStore = preview === null ? null : defaultAcquiredStore(preview.shopName);
  const storeOptions = (() => {
    const base = STORE_KEYS.map((value) => ({ value, label: storeLabel(value) }));
    if (derivedStore !== null && !base.some((option) => option.value === derivedStore)) {
      return [{ value: derivedStore, label: preview?.shopName ?? derivedStore }, ...base];
    }
    return base;
  })();

  function applyTier(nextKey: string | null, nextSelected: readonly string[]) {
    tierKeyRef.current = nextKey;
    selectedRef.current = nextSelected;
    setTierKey(nextKey);
    setSelected(nextSelected);
  }

  function applySelected(next: readonly string[]) {
    selectedRef.current = next;
    setSelected(next);
  }

  async function loadPreview(nextPlatform: string) {
    const seq = ++previewSeq.current;
    previewAbort.current?.abort();
    const controller = new AbortController();
    previewAbort.current = controller;
    setLoading(true);
    setLoadError(null);
    try {
      const next = await getBundlePurchasePreview(target.source, target.bundleKey, nextPlatform, controller.signal);
      if (seq !== previewSeq.current) return;
      setPreview(next);

      // La selección sobrevive a una recarga (cambio de plataforma o snapshot viejo): la intención del
      // usuario no se pierde porque el servidor haya vuelto a mandar la misma membresía.
      const previousTierKey = tierKeyRef.current;
      const keepTier = previousTierKey !== null && next.tiers.some((tier) => tier.tierKey === previousTierKey);
      const wantedKey = keepTier ? previousTierKey : defaultTierKey(next);
      const nextTier = next.tiers.find((tier) => tier.tierKey === wantedKey) ?? null;
      applyTier(nextTier?.tierKey ?? null, keepTier ? selectedRef.current : initialSelection(nextTier));
      setLinkTarget(null);

      if (!storeInitialized.current) {
        storeInitialized.current = true;
        setStoreChoice(defaultAcquiredStore(next.shopName) ?? "");
      }
      if (next.purchased && !purchasedNotified.current) {
        purchasedNotified.current = true;
        onPurchased();
      }
    } catch (cause) {
      if (seq !== previewSeq.current) return;
      if (cause instanceof DOMException && cause.name === "AbortError") return;
      setLoadError(cause instanceof Error ? cause.message : "No se pudo leer la vista previa del bundle.");
    } finally {
      if (seq === previewSeq.current) setLoading(false);
    }
  }

  useEffect(() => {
    void loadPreview(PURCHASE_PLATFORM_DEFAULT);
    return () => {
      previewSeq.current += 1;
      previewAbort.current?.abort();
    };
    // Solo al montar: la recarga es explícita (plataforma, reintento o snapshot viejo).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Contención de foco y Escape, igual que el diálogo de alta de la wishlist. `busy` se lee por ref para
  // que enviar la compra no vuelva a montar el efecto ni robe el foco en medio de la petición.
  useEffect(() => {
    panelRef.current?.focus();

    function onKeyDown(event: globalThis.KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        if (!busyRef.current) onCloseRef.current();
        return;
      }
      if (event.key !== "Tab" || !panelRef.current) return;
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

  // Cuando el formulario desaparece por el feedback, el foco se quedaría en el <body>.
  useEffect(() => {
    if (feedback !== null) closeButtonRef.current?.focus();
  }, [feedback]);

  function changePlatform(next: string) {
    setPlatform(next);
    setSubmitError(null);
    // La posesión que muestra el servidor depende de la plataforma: se vuelve a pedir la vista previa sin
    // perder la selección ni el vínculo manual ya elegido.
    void loadPreview(next);
  }

  function changeTier(tier: BundlePurchasePreviewTier) {
    if (!tier.importable) return;
    applyTier(tier.tierKey, initialSelection(tier));
    setLinkTarget(null);
    setSubmitError(null);
  }

  function toggleItem(itemKey: string) {
    const attempt = toggleSelection(activeTier, selectedRef.current, itemKey);
    if (attempt.ok) {
      applySelected(attempt.selected);
      setSubmitError(null);
      return;
    }
    setSubmitError(attempt.message);
  }

  async function submitPurchase() {
    if (busyRef.current) return;
    if (preview === null) return;

    const effectiveStore = storeChoice === STORE_OTHER ? customStore : storeChoice;
    const validation = validatePurchaseConfirm({
      preview,
      tier: activeTier,
      selectedKeys: selected,
      itemPlatforms,
      manualLinks,
      platform,
      acquiredAt,
      acquiredStore: effectiveStore,
      acquiredStoreType: storeType,
      acquisitionProvider: provider,
      now: new Date()
    });
    if (!validation.ok) {
      setSubmitError(validation.error);
      return;
    }

    busyRef.current = true;
    setBusy(true);
    setSubmitError(null);
    try {
      const purchase = await createBundlePurchase(validation.body);
      const summary = purchaseFeedback(purchase);
      setFeedback(summary);
      setFeedbackDetails([
        ...summary.details,
        ...(purchase.games.some((game) => game.wishlistRemoved)
          ? ["Los juegos que ya estaban en tu wishlist local se quitaron con protección contra la próxima sincronización de Steam."]
          : [])
      ]);
      setResultGames(
        purchase.games.map((game) => ({
          itemKey: game.itemKey,
          title: game.title,
          platform: game.platform,
          outcome: game.outcome,
          wishlistRemoved: game.wishlistRemoved
        }))
      );
      onPurchased();
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 400) {
        // Un 400 significa snapshot viejo, conteo equivocado, vínculo inválido o un ítem sin título. El
        // servidor es la autoridad y su motivo se muestra textual; además se vuelve a pedir la vista previa,
        // que es la salida del caso mayoritario (el bundle cambió). El mensaje evita un bucle a ciegas si lo
        // que falla no lo arregla refrescar.
        const reason = cause.message?.trim();
        setSubmitError(null);
        setStaleNotice(
          reason
            ? `${reason} Actualizamos la vista previa: revisa el tier y los juegos antes de confirmar.`
            : "El bundle cambió desde que abriste esta vista previa. La actualizamos: revisa el tier y los juegos antes de confirmar."
        );
        await loadPreview(platform);
      } else if (cause instanceof ApiError && cause.status === 404) {
        setSubmitError("Ese bundle ya no está disponible para registrar su compra.");
      } else if (cause instanceof ApiError && cause.status === 409) {
        setSubmitError("Otra operación está registrando este bundle. Reintenta en un momento.");
      } else {
        setSubmitError(cause instanceof Error ? cause.message : "No se pudo registrar la compra del bundle.");
      }
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    void submitPurchase();
  }

  const observed = preview === null ? null : formatObserved(preview.observedAt);
  const noTiers = preview !== null && preview.tiers.length === 0;

  return (
    <div
      className="fixed inset-0 z-[70] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={() => {
        if (!busyRef.current) onClose();
      }}
    >
      <section
        ref={panelRef}
        tabIndex={-1}
        className="app-card max-h-[92dvh] w-full max-w-2xl space-y-4 overflow-y-auto p-5 outline-none"
        role="dialog"
        aria-modal="true"
        aria-labelledby="bundle-purchase-title"
        aria-describedby="bundle-purchase-subtitle"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 id="bundle-purchase-title" className="text-lg font-semibold text-primary">
              Registrar la compra del bundle
            </h2>
            <p id="bundle-purchase-subtitle" className="mt-0.5 text-xs text-muted">
              <span className="font-semibold text-primary">{preview?.title ?? target.title}</span>
              {preview?.shopName ? ` · ${preview.shopName}` : null}
              {observed ? ` · Observado ${observed}` : null}. Al confirmar, el bundle sale de pendientes y sus
              juegos pasan a tu biblioteca.
            </p>
          </div>
          <Button type="button" variant="ghost" onClick={onClose} aria-label="Cerrar" disabled={busy}>
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </Button>
        </div>

        {staleNotice ? <Alert variant="info">{staleNotice}</Alert> : null}
        {submitError ? <Alert variant="danger">{submitError}</Alert> : null}

        {loading && preview === null ? (
          <p className="text-sm text-muted" aria-live="polite">
            Leyendo la vista previa del bundle...
          </p>
        ) : null}

        {loadError ? (
          <div className="space-y-3">
            <Alert variant="danger">{loadError}</Alert>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="secondary" loading={loading} onClick={() => void loadPreview(platform)}>
                Reintentar
              </Button>
              <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>
                Cerrar
              </Button>
            </div>
          </div>
        ) : null}

        {preview !== null && preview.purchased ? (
          <div className="space-y-3">
            <Alert variant="info">
              Ya registramos esta compra{preview.purchaseId !== null ? ` (#${preview.purchaseId})` : ""}. Podrás verla
              en el historial y sus juegos duplicados en Keys duplicadas.
            </Alert>
            <Button ref={closeButtonRef} type="button" variant="primary" onClick={onClose}>
              Cerrar
            </Button>
          </div>
        ) : null}

        {preview !== null && !preview.purchased && feedback !== null ? (
          <div className="space-y-3">
            <p
              role="status"
              aria-live="polite"
              className={
                feedback.tone === "success" ? "text-sm font-semibold text-success" : "text-sm font-semibold text-primary"
              }
            >
              {feedback.headline}
            </p>
            <ul className="list-disc space-y-1 pl-5 text-xs text-secondary">
              {feedbackDetails.map((detail) => (
                <li key={detail}>{detail}</li>
              ))}
            </ul>
            {resultGames.length > 0 ? (
              <div className="space-y-1">
                <p className="text-xs font-semibold uppercase tracking-widest text-muted">Juegos reportados</p>
                <ul className="space-y-1">
                  {resultGames.map((game) => (
                    <li key={game.itemKey} className="flex flex-wrap items-center gap-1.5 text-xs">
                      <span className="font-semibold text-primary">{game.title}</span>
                      {game.platform ? <span className="tabler-badge tabler-badge-muted">{storeLabel(game.platform)}</span> : null}
                      <span className="tabler-badge tabler-badge-info">{outcomeLabel(game.outcome)}</span>
                      {game.wishlistRemoved ? (
                        <span className="tabler-badge tabler-badge-success">Fuera de tu wishlist local</span>
                      ) : null}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
            <p className="text-xs text-muted">
              La lista de bundles pendientes y el historial ya se actualizaron. No se guardan códigos de
              activación: solo el juego, su plataforma y el bundle del que salió.
            </p>
            <Button ref={closeButtonRef} type="button" variant="primary" onClick={onClose}>
              Cerrar
            </Button>
          </div>
        ) : null}

        {preview !== null && !preview.purchased && feedback === null ? (
          <form className="space-y-4" onSubmit={onSubmit} noValidate>
            {noTiers ? (
              <Alert variant="danger">
                El servidor no publicó ningún tier para este bundle: no hay nada que registrar todavía.
              </Alert>
            ) : (
              <>
                <fieldset className="space-y-2">
                  <legend className="text-xs font-semibold uppercase tracking-widest text-muted">
                    Tier que compraste
                  </legend>
                  <ul className="space-y-2">
                    {preview.tiers.map((tier) => {
                      const price = formatMinor(tier.priceMinor, tier.currency);
                      const blocked = tier.importable
                        ? null
                        : tier.blockedReason === null
                          ? "Este tier no se puede registrar todavía."
                          : TIER_BLOCKED_REASON_LABELS[tier.blockedReason];
                      return (
                        <li key={tier.tierKey}>
                          <label
                            className={
                              tier.importable
                                ? "flex cursor-pointer items-start gap-2 rounded-[var(--radius-md)] border border-default p-3 text-sm"
                                : "flex items-start gap-2 rounded-[var(--radius-md)] border border-dashed border-default p-3 text-sm opacity-80"
                            }
                          >
                            <input
                              type="radio"
                              name="bundle-purchase-tier"
                              value={tier.tierKey}
                              checked={tierKey === tier.tierKey}
                              disabled={!tier.importable || busy}
                              onChange={() => changeTier(tier)}
                              className="mt-0.5 h-4 w-4 shrink-0 accent-[var(--color-accent)]"
                            />
                            <span className="min-w-0 space-y-1">
                              <span className="flex flex-wrap items-baseline gap-2">
                                <span className="font-semibold text-primary">Tier {tier.tierIndex + 1}</span>
                                {price !== null ? <span className="font-semibold text-primary">{price}</span> : null}
                                {tier.currency ? (
                                  <span className="text-xs font-semibold uppercase text-muted">{tier.currency}</span>
                                ) : null}
                                {tier.addon ? <span className="tabler-badge tabler-badge-warning">Addon</span> : null}
                                {!tier.itemsComplete ? (
                                  <span className="tabler-badge tabler-badge-warning">Contenido incompleto</span>
                                ) : null}
                              </span>
                              <span className="block text-xs text-muted">{tierModeText(tier)}</span>
                              {blocked ? <span className="block text-xs text-muted">{blocked}</span> : null}
                            </span>
                          </label>
                        </li>
                      );
                    })}
                  </ul>
                </fieldset>

                <Select
                  id="bundle-purchase-platform"
                  label="Plataforma de la compra"
                  value={platform}
                  disabled={busy}
                  aria-describedby="bundle-purchase-platform-help"
                  onChange={(event) => changePlatform(event.target.value)}
                >
                  {PURCHASE_PLATFORM_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </Select>
                <p id="bundle-purchase-platform-help" className="text-xs text-muted">
                  Plataforma de todas las claves del bundle; puedes ajustarla juego por juego abajo. El selector
                  cubre las tiendas que sigue la app y la tienda donde compraste se pregunta aparte.
                </p>

                {activeTier !== null ? (
                  <div className="space-y-2">
                    <p className="flex flex-wrap items-center gap-2 text-xs">
                      <span className="font-semibold uppercase tracking-widest text-muted">Juegos del tier</span>
                      <span className={status.complete ? "tabler-badge tabler-badge-success" : "tabler-badge tabler-badge-warning"}>
                        {status.message}
                      </span>
                    </p>
                    <ul className="space-y-2">
                      {activeTier.items.map((item) => (
                        <ItemRow
                          key={item.itemKey}
                          item={item}
                          tier={activeTier}
                          willImport={importedKeys.includes(item.itemKey)}
                          atLimit={atLimit === true}
                          busy={busy}
                          platform={platform}
                          platformOverride={itemPlatforms[item.itemKey]}
                          link={manualLinks[item.itemKey]}
                          linkOpen={linkTarget === item.itemKey}
                          onToggle={() => toggleItem(item.itemKey)}
                          onPlatformChange={(value) =>
                            setItemPlatforms((current) => {
                              const next = { ...current };
                              if (value === "") delete next[item.itemKey];
                              else next[item.itemKey] = value;
                              return next;
                            })
                          }
                          onOpenLink={() => setLinkTarget(item.itemKey)}
                          onClearLink={() =>
                            setManualLinks((current) => {
                              const next = { ...current };
                              delete next[item.itemKey];
                              return next;
                            })
                          }
                          onPickLink={(candidate) => {
                            setManualLinks((current) => ({
                              ...current,
                              [item.itemKey]: { gameId: candidate.gameId, title: candidate.title }
                            }));
                            setLinkTarget(null);
                            setSubmitError(null);
                          }}
                          onCancelLink={() => setLinkTarget(null)}
                        />
                      ))}
                    </ul>
                    {pendingLinks.length > 0 ? (
                      <p className="text-xs text-[var(--color-danger)]">
                        {pendingLinks.length === 1
                          ? `Falta vincular «${pendingLinks[0]?.title}» antes de confirmar.`
                          : `Faltan ${pendingLinks.length} vínculos antes de confirmar.`}
                      </p>
                    ) : null}
                  </div>
                ) : (
                  <p className="text-xs text-muted">
                    Elige un tier que se pueda registrar para ver sus juegos.
                  </p>
                )}

                <div className="space-y-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-3">
                  <p className="text-xs font-semibold uppercase tracking-widest text-muted">Datos de la compra</p>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <div className="grid gap-1.5 text-sm font-medium text-primary">
                      <label htmlFor="bundle-purchase-date">Fecha de compra</label>
                      <input
                        id="bundle-purchase-date"
                        type="date"
                        value={acquiredAt}
                        disabled={busy}
                        onChange={(event) => {
                          setAcquiredAt(event.target.value);
                          setSubmitError(null);
                        }}
                        className="input-semantic h-10 w-full px-3 text-sm"
                      />
                    </div>
                    <Select
                      id="bundle-purchase-store"
                      label="Tienda donde lo compraste"
                      value={storeChoice}
                      disabled={busy}
                      aria-describedby="bundle-purchase-store-help"
                      onChange={(event) => {
                        setStoreChoice(event.target.value);
                        setSubmitError(null);
                      }}
                    >
                      <option value="">Elige la tienda…</option>
                      {storeOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                      <option value={STORE_OTHER}>Otra tienda…</option>
                    </Select>
                  </div>

                  {storeChoice === STORE_OTHER ? (
                    <div className="space-y-1.5">
                      <label htmlFor="bundle-purchase-custom-store" className="text-sm font-medium text-primary">
                        Tu tienda
                      </label>
                      <input
                        id="bundle-purchase-custom-store"
                        type="text"
                        value={customStore}
                        maxLength={32}
                        autoComplete="off"
                        disabled={busy}
                        onChange={(event) => setCustomStore(event.target.value)}
                        placeholder="tienda_local"
                        className="input-semantic h-10 w-full px-3 text-sm"
                      />
                      <p className="text-xs text-muted">En minúsculas y sin espacios: letras, números, punto, guion o guion bajo.</p>
                    </div>
                  ) : null}

                  <div className="grid gap-3 sm:grid-cols-2">
                    <Select
                      id="bundle-purchase-store-type"
                      label="Tipo de tienda"
                      value={storeType}
                      disabled={busy}
                      onChange={(event) => {
                        setStoreType(event.target.value);
                        setSubmitError(null);
                      }}
                    >
                      {ACQUIRED_STORE_TYPE_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </Select>
                    <Select
                      id="bundle-purchase-provider"
                      label="Oferta encontrada en"
                      value={provider}
                      disabled={busy}
                      onChange={(event) => {
                        setProvider(event.target.value);
                        setSubmitError(null);
                      }}
                    >
                      {ACQUISITION_PROVIDER_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </Select>
                  </div>
                  <p id="bundle-purchase-store-help" className="text-xs text-muted">
                    Son hechos que declaras: la plataforma va aparte y el proveedor donde viste la oferta no decide
                    si la tienda es oficial. No se muestran ni se guardan códigos de activación.
                  </p>
                </div>

                <div className="flex flex-wrap items-center gap-3">
                  <Button
                    type="submit"
                    variant="primary"
                    loading={busy}
                    loadingText="Registrando..."
                    disabled={
                      loading || activeTier === null || !activeTier.importable || preview.tiers.length === 0
                    }
                  >
                    Registrar compra e importar juegos
                  </Button>
                  <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>
                    Cancelar
                  </Button>
                  <span className="text-xs text-muted" aria-live="polite">
                    {busy
                      ? "Guardando la compra y los juegos..."
                      : loading
                        ? "Actualizando la vista previa del bundle..."
                        : submitError !== null
                          ? "Corrige lo que indica el aviso y vuelve a intentar: no se registró nada."
                          : "Se importan los juegos elegibles; los duplicados se guardan en Keys duplicadas."}
                  </span>
                </div>
              </>
            )}
          </form>
        ) : null}
      </section>
    </div>
  );
}
