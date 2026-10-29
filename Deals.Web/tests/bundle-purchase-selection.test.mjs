import test from "node:test";
import assert from "node:assert/strict";
import { normalizeBundlePurchasePreview } from "../lib/contracts/bundle-purchases.ts";
import {
  ACQUISITION_PROVIDER_OPTIONS,
  ACQUIRED_STORE_TYPE_OPTIONS,
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
  validatePurchaseConfirm
} from "../app/bundles/_lib/bundle-purchase.ts";

// S4/S5: reglas puras de la compra de un bundle. Fijo = se importan los juegos elegibles tal como vienen;
// selección = exactamente N juegos elegidos, ni uno más ni uno menos; un ítem sin identidad no se puede
// confirmar sin vínculo manual. Nada de red, nada de reloj: `now` se inyecta.

const HASH = "b".repeat(64);
const NOW = new Date("2026-10-20T18:00:00Z");

function item(overrides = {}) {
  return {
    itemKey: "item-1",
    title: "Juego Uno",
    type: "game",
    priceMinor: null,
    currency: null,
    itadGameId: "itad-uuid-1",
    url: null,
    eligible: true,
    canonicalGameId: null,
    canonicalSource: null,
    requiresManualLink: false,
    owned: null,
    ownedUserLibraryId: null,
    ownedPlatform: null,
    ...overrides
  };
}

function tier(overrides = {}) {
  return {
    tierKey: "tier-1",
    tierIndex: 0,
    priceMinor: 1299,
    currency: "USD",
    addon: false,
    itemsComplete: true,
    selectionCount: null,
    selectionMode: "fixed",
    requiredSelectionCount: null,
    importable: true,
    blockedReason: null,
    items: [item()],
    ...overrides
  };
}

function previewOf(tiers) {
  const preview = normalizeBundlePurchasePreview({
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega Bundle",
    shopId: 12,
    shopName: "Fanatical",
    pageUrl: "https://isthereanydeal.com/bundle/x",
    dealUrl: null,
    details: null,
    publishedAt: "2026-10-01T10:00:00Z",
    expiresAt: "2026-10-30T10:00:00Z",
    observedAt: "2026-10-19T10:00:00Z",
    snapshotHash: HASH,
    purchased: false,
    purchaseId: null,
    tiers
  });
  assert.ok(preview, "la vista previa de la prueba debe normalizar");
  return preview;
}

const fixedPreview = previewOf([
  tier({
    tierKey: "fixed",
    items: [item({ itemKey: "a" }), item({ itemKey: "b" }), item({ itemKey: "ost", eligible: false, type: "music" })]
  })
]);

const choicePreview = previewOf([
  tier({
    tierKey: "choice",
    selectionMode: "choice",
    requiredSelectionCount: 2,
    selectionCount: 2,
    items: [item({ itemKey: "a" }), item({ itemKey: "b" }), item({ itemKey: "c" })]
  })
]);

function tierOf(preview, tierKey) {
  return preview.tiers.find((candidate) => candidate.tierKey === tierKey) ?? null;
}

function confirmInput(overrides = {}) {
  const preview = overrides.preview ?? fixedPreview;
  return {
    preview,
    tier: overrides.tier ?? tierOf(preview, preview.tiers[0].tierKey),
    selectedKeys: [],
    itemPlatforms: {},
    manualLinks: {},
    platform: "steam",
    acquiredAt: "2026-10-20",
    acquiredStore: "fanatical",
    acquiredStoreType: "official",
    acquisitionProvider: "itad",
    now: NOW,
    ...overrides
  };
}

// --- defaults -------------------------------------------------------------

test("la plataforma por defecto es Steam y el selector usa solo llaves canónicas", () => {
  assert.equal(PURCHASE_PLATFORM_DEFAULT, "steam");
  const values = PURCHASE_PLATFORM_OPTIONS.map((option) => option.value);
  assert.ok(values.includes("steam"));
  assert.ok(values.includes("gog"));
  assert.equal(new Set(values).size, values.length, "sin opciones repetidas");
  // El backend valida StoreKeys: el selector no puede ofrecer nada fuera de ese vocabulario.
  assert.ok(!values.includes("switch"));
});

test("el tier por defecto es el primero importable, y sin importables se muestra el primero", () => {
  const mixed = previewOf([
    tier({ tierKey: "addon", importable: false, blockedReason: "addon_tier" }),
    tier({ tierKey: "bueno", tierIndex: 1 })
  ]);
  assert.equal(defaultTierKey(mixed), "bueno");
  assert.equal(defaultTierKey(fixedPreview), "fixed");
  assert.equal(defaultTierKey(previewOf([])), null);
});

// --- selección ------------------------------------------------------------

test("un tier fijo arranca con todos sus juegos elegibles y no deja deseleccionar", () => {
  const tier = tierOf(fixedPreview, "fixed");
  assert.deepEqual(initialSelection(tier), ["a", "b"]);

  const attempt = toggleSelection(tier, initialSelection(tier), "a");
  assert.equal(attempt.ok, false);
  assert.match(attempt.message, /fijo/i);
  assert.match(attempt.message, /automáticamente/);
});

test("un tier de selección arranca vacío y no admite un juego de más", () => {
  const tier = tierOf(choicePreview, "choice");
  assert.deepEqual(initialSelection(tier), []);

  const first = toggleSelection(tier, [], "a");
  assert.equal(first.ok, true);
  assert.deepEqual(first.selected, ["a"]);

  const second = toggleSelection(tier, ["a"], "b");
  assert.equal(second.ok, true);
  assert.deepEqual(second.selected, ["a", "b"], "se conserva el orden del tier, no el de los clics");

  const third = toggleSelection(tier, second.selected, "c");
  assert.equal(third.ok, false, "el cupo exacto no se puede pasar");
  assert.match(third.message, /exactamente 2/);
  assert.match(third.message, /ya elegiste 2/);
});

test("un tier de selección deja corregir la selección desmarcando", () => {
  const tier = tierOf(choicePreview, "choice");
  const removed = toggleSelection(tier, ["b", "a"], "a");
  assert.equal(removed.ok, true);
  assert.deepEqual(removed.selected, ["b"]);
});

test("un ítem no elegible no se puede seleccionar en un tier de selección", () => {
  const choiceTier = tierOf(
    previewOf([
      tier({
        tierKey: "x",
        selectionMode: "choice",
        requiredSelectionCount: 1,
        items: [item({ itemKey: "a" }), item({ itemKey: "dlc", eligible: false, type: "dlc" })]
      })
    ]),
    "x"
  );
  const attempt = toggleSelection(choiceTier, [], "dlc");
  assert.equal(attempt.ok, false);
  assert.match(attempt.message, /no es un juego elegible/i);
});

test("el estado de selección solo está completo con el cupo exacto", () => {
  const tier = tierOf(choicePreview, "choice");
  assert.equal(selectionStatus(tier, []).complete, false);
  assert.equal(selectionStatus(tier, ["a"]).complete, false);
  assert.match(selectionStatus(tier, ["a"]).message, /1 de 2/);
  assert.equal(selectionStatus(tier, ["a", "b"]).complete, true);

  const fixed = tierOf(fixedPreview, "fixed");
  assert.equal(selectionStatus(fixed, []).complete, true, "un tier fijo no necesita clics");
  assert.equal(selectionStatus(fixed, []).selectedCount, 2);
});

test("un ítem sin identidad queda pendiente de vínculo y uno identificado no", () => {
  const preview = previewOf([
    tier({
      items: [
        item({ itemKey: "itad" }),
        item({ itemKey: "sin-id", itadGameId: null, title: "Juego Huérfano" })
      ]
    })
  ]);
  const previewTier = preview.tiers[0];
  const pending = unlinkedItems(previewTier, ["itad", "sin-id"], {});
  assert.equal(pending.length, 1);
  assert.equal(pending[0]?.title, "Juego Huérfano");

  assert.equal(
    unlinkedItems(previewTier, ["itad", "sin-id"], { "sin-id": { gameId: 501, title: "Ficha" } }).length,
    0
  );
});

// --- confirmación ---------------------------------------------------------

test("un tier fijo confirma todos sus elegibles y deja los identificados en null", () => {
  const validation = validatePurchaseConfirm(confirmInput({ selectedKeys: [] }));
  assert.equal(validation.ok, true, validation.ok ? "" : validation.error);
  assert.deepEqual(validation.body.items, [
    { itemKey: "a", platform: null, canonicalGameId: null },
    { itemKey: "b", platform: null, canonicalGameId: null }
  ]);
  assert.equal(validation.body.platform, "steam");
  assert.equal(validation.body.snapshotHash, HASH);
  assert.equal(validation.body.tierKey, "fixed");
  assert.equal(validation.body.items.length, 2, "el tercer ítem es un OST y no entra");
});

test("un tier de selección exige exactamente su cupo y solo manda los elegidos", () => {
  const short = validatePurchaseConfirm(confirmInput({ preview: choicePreview, selectedKeys: ["a"] }));
  assert.equal(short.ok, false);
  assert.match(short.error, /exactamente 2/);

  const exact = validatePurchaseConfirm(
    confirmInput({ preview: choicePreview, selectedKeys: ["a", "c"] })
  );
  assert.equal(exact.ok, true, exact.ok ? "" : exact.error);
  assert.deepEqual(
    exact.body.items.map((entry) => entry.itemKey),
    ["a", "c"]
  );
});

test("una clave que ya no existe en el tier no bloquea: el cupo exacto decide", () => {
  // Tras recargar la vista previa, la membresía puede cambiar. Un clic viejo no puede dejar el diálogo en un
  // callejón: la clave desaparecida se descarta y el cupo exacto vuelve a comprobarse.
  const exact = validatePurchaseConfirm(
    confirmInput({ preview: choicePreview, selectedKeys: ["a", "b", "fantasma"] })
  );
  assert.equal(exact.ok, true, exact.ok ? "" : exact.error);
  assert.deepEqual(
    exact.body.items.map((entry) => entry.itemKey),
    ["a", "b"]
  );

  const short = validatePurchaseConfirm(
    confirmInput({ preview: choicePreview, selectedKeys: ["fantasma", "a"] })
  );
  assert.equal(short.ok, false);
  assert.match(short.error, /exactamente 2/);

  // Un tier fijo ni siquiera mira la selección: su membresía es la del servidor.
  const fixed = validatePurchaseConfirm(confirmInput({ selectedKeys: ["fantasma"] }));
  assert.equal(fixed.ok, true, fixed.ok ? "" : fixed.error);
  assert.equal(fixed.body.items.length, 2);
});

test("un juego sin identidad no se confirma sin vínculo manual", () => {
  const preview = previewOf([
    tier({
      items: [item({ itemKey: "sin-id", itadGameId: null, title: "Juego Huérfano" })]
    })
  ]);
  const missing = validatePurchaseConfirm(confirmInput({ preview }));
  assert.equal(missing.ok, false);
  assert.match(missing.error, /Juego Huérfano/);
  assert.match(missing.error, /vincular/i);

  const linked = validatePurchaseConfirm(
    confirmInput({
      preview,
      manualLinks: { "sin-id": { gameId: 501, title: "Juego Canónico" } }
    })
  );
  assert.equal(linked.ok, true, linked.ok ? "" : linked.error);
  assert.deepEqual(linked.body.items[0], { itemKey: "sin-id", platform: null, canonicalGameId: 501 });
});

test("una ficha vinculada a mano nunca reemplaza la identidad que ITAD ya tiene", () => {
  const validation = validatePurchaseConfirm(
    confirmInput({ manualLinks: { a: { gameId: 999, title: "Otra ficha" } } })
  );
  assert.equal(validation.ok, true, validation.ok ? "" : validation.error);
  assert.deepEqual(
    validation.body.items.map((entry) => entry.canonicalGameId),
    [null, null],
    "un ítem identificado manda null, según el contrato"
  );
});

test("el override de plataforma por juego viaja y se valida", () => {
  const overridden = validatePurchaseConfirm(confirmInput({ itemPlatforms: { a: "gog" } }));
  assert.equal(overridden.ok, true, overridden.ok ? "" : overridden.error);
  assert.deepEqual(overridden.body.items, [
    { itemKey: "a", platform: "gog", canonicalGameId: null },
    { itemKey: "b", platform: null, canonicalGameId: null }
  ]);

  const slug = validatePurchaseConfirm(confirmInput({ itemPlatforms: { a: "switch" } }));
  assert.equal(slug.ok, true, slug.ok ? "" : slug.error);
  assert.equal(slug.body.items[0]?.platform, "switch");

  const invalid = validatePurchaseConfirm(confirmInput({ itemPlatforms: { a: "Nintendo Switch" } }));
  assert.equal(invalid.ok, false);
  assert.match(invalid.error, /plataforma/i);
});

test("un tier no importable nunca confirma y explica el motivo", () => {
  const preview = previewOf([
    tier({ tierKey: "addon", importable: false, blockedReason: "addon_tier" })
  ]);
  const validation = validatePurchaseConfirm(confirmInput({ preview }));
  assert.equal(validation.ok, false);
  assert.equal(validation.error, TIER_BLOCKED_REASON_LABELS.addon_tier);
  assert.match(validation.error, /addon/i);
});

test("la plataforma de la compra se exige ya canónica", () => {
  // El backend normaliza con StoreKeys (tiendas + slugs de plataforma) y vuelve a validar; aquí se exige
  // la llave tal cual para no reescribir una intención que el usuario no eligió.
  const canonicalSlug = validatePurchaseConfirm(confirmInput({ platform: "switch" }));
  assert.equal(canonicalSlug.ok, true, canonicalSlug.ok ? "" : canonicalSlug.error);
  assert.equal(canonicalSlug.body.platform, "switch");

  for (const platform of ["Switch", "Nintendo Switch", "no vale", ""]) {
    const validation = validatePurchaseConfirm(confirmInput({ platform }));
    assert.equal(validation.ok, false, platform);
    assert.match(validation.error, /plataforma/i);
  }
});

test("la fecha de compra es un día real y no puede ser futura", () => {
  const future = validatePurchaseConfirm(confirmInput({ acquiredAt: "2026-10-21" }));
  assert.equal(future.ok, false);
  assert.match(future.error, /futura/i);

  const malformed = validatePurchaseConfirm(confirmInput({ acquiredAt: "2026-02-30" }));
  assert.equal(malformed.ok, false);

  const past = validatePurchaseConfirm(confirmInput({ acquiredAt: "2026-10-19" }));
  assert.equal(past.ok, true, past.ok ? "" : past.error);
});

test("la tienda, el tipo de tienda y el proveedor son hechos declarados y cerrados", () => {
  const normalized = validatePurchaseConfirm(confirmInput({ acquiredStore: "Fanatical" }));
  assert.equal(normalized.ok, true, normalized.ok ? "" : normalized.error);
  assert.equal(normalized.body.acquiredStore, "fanatical");

  const badStore = validatePurchaseConfirm(confirmInput({ acquiredStore: "" }));
  assert.equal(badStore.ok, false);
  assert.match(badStore.error, /tienda/i);

  const badType = validatePurchaseConfirm(confirmInput({ acquiredStoreType: "gris" }));
  assert.equal(badType.ok, false);

  const badProvider = validatePurchaseConfirm(confirmInput({ acquisitionProvider: "web" }));
  assert.equal(badProvider.ok, false);

  assert.equal(
    validatePurchaseConfirm(confirmInput({ acquiredStoreType: "keyshop", acquisitionProvider: "ggdeals" })).ok,
    true
  );
});

test("el cuerpo validado es exactamente el que el contrato del BFF acepta", () => {
  const validation = validatePurchaseConfirm(
    confirmInput({ preview: choicePreview, selectedKeys: ["b", "c"], itemPlatforms: { c: "epic" } })
  );
  assert.equal(validation.ok, true, validation.ok ? "" : validation.error);
  // La comprobación final del módulo puro usa el mismo normalizador del BFF: una divergencia aquí se ve
  // como un fallo, no como un 400 del servidor.
  assert.deepEqual(validation.body.items, [
    { itemKey: "b", platform: null, canonicalGameId: null },
    { itemKey: "c", platform: "epic", canonicalGameId: null }
  ]);
});

// --- fechas y defaults ----------------------------------------------------

test("la fecha por defecto nunca se adelanta al día UTC del servidor", () => {
  assert.equal(acquisitionDateDefault(new Date(2026, 9, 20, 18, 0)), "2026-10-20");
  // Un instante en el que el día local ya cambió pero UTC no: se elige el día UTC, que el backend acepta.
  const lateLocal = new Date(2026, 9, 21, 0, 30);
  assert.equal(acquisitionDateDefault(lateLocal), "2026-10-21");
});

test("la tienda por defecto sale del nombre del shop sin afirmar plataforma", () => {
  assert.equal(defaultAcquiredStore("Fanatical"), "fanatical");
  assert.equal(defaultAcquiredStore("Humble Bundle"), "humble-bundle");
  assert.equal(defaultAcquiredStore("Steam"), "steam");
  assert.equal(defaultAcquiredStore(null), null);
  assert.equal(defaultAcquiredStore("¿?"), null);
});

test("el proveedor por defecto sale de la fuente del bundle", () => {
  assert.equal(defaultProviderForSource("ggdeals"), "ggdeals");
  assert.equal(defaultProviderForSource("itad"), "itad");
  assert.equal(defaultProviderForSource("otra"), "other");
});

test("los selectores declaran los mismos hechos que la biblioteca", () => {
  assert.deepEqual(
    ACQUIRED_STORE_TYPE_OPTIONS.map((option) => option.value),
    ["official", "keyshop", "unknown"]
  );
  assert.deepEqual(
    ACQUISITION_PROVIDER_OPTIONS.map((option) => option.value),
    ["itad", "ggdeals", "direct", "other"]
  );
});

// --- copy del resultado ---------------------------------------------------

test("una compra nueva cuenta lo importado y lo duplicado sin prometer códigos", () => {
  const feedback = purchaseFeedback({
    purchaseId: 9,
    created: true,
    alreadyReported: false,
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega",
    tierKey: "tier-1",
    platform: "steam",
    snapshotHash: HASH,
    selectedCount: 3,
    importedCount: 2,
    duplicateCount: 1,
    wishlistRemovedCount: 1,
    games: []
  });
  assert.equal(feedback.tone, "success");
  assert.equal(feedback.headline, "Compra registrada.");
  const text = feedback.details.join(" ");
  assert.match(text, /2 juegos nuevos/);
  assert.match(text, /1 juego ya estaba/);
  assert.match(text, /wishlist local/);
  assert.doesNotMatch(text, /c[oó]digo/i, "este cliente no maneja códigos de activación");
});

test("un reenvío no se anuncia como una segunda compra", () => {
  const feedback = purchaseFeedback({
    purchaseId: 9,
    created: false,
    alreadyReported: true,
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega",
    tierKey: "tier-1",
    platform: "steam",
    snapshotHash: HASH,
    selectedCount: 3,
    importedCount: 2,
    duplicateCount: 1,
    wishlistRemovedCount: 1,
    games: []
  });
  assert.equal(feedback.tone, "info");
  assert.match(feedback.headline, /ya estaba registrada/i);
  assert.match(feedback.details.join(" "), /No se escribió nada nuevo/);
});

test("el resultado por juego se etiqueta sin inventar vocabulario", () => {
  assert.equal(outcomeLabel("imported"), "Importado");
  assert.equal(outcomeLabel("duplicate"), "Duplicado");
  assert.equal(outcomeLabel("raro_del_servidor"), "raro_del_servidor", "un código desconocido se muestra tal cual");
  assert.equal(outcomeLabel(null), "Registrado");
  assert.equal(eligibilityLabel(item()), "Se importa");
  assert.equal(eligibilityLabel(item({ eligible: false, type: "dlc" })), "No elegible (dlc)");
});
