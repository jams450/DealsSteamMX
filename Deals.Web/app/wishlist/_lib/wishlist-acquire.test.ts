import test from "node:test";
import assert from "node:assert/strict";
import {
  ACQUIRED_STORE_TYPE_OPTIONS,
  ACQUISITION_PROVIDER_OPTIONS,
  ACQUISITION_TYPE_OPTIONS,
  ACQUIRE_PLATFORM_OPTIONS,
  ACQUIRE_PROVIDER_DEFAULT,
  ACQUIRE_PROVIDER_LABEL,
  ACQUIRE_REMOVE_DEFAULT,
  ACQUIRE_STORE_OPTIONS,
  ACQUIRE_STORE_TYPE_DEFAULT,
  ACQUIRE_STORE_TYPE_HINT,
  ACQUIRE_STORE_TYPE_LABEL,
  STEAM_REMOVAL_NOTICE,
  STEAM_REMOVAL_SYNC_NOTE,
  acquisitionFeedback,
  normalizeAcquireRequestBody,
  parseAcquisitionDate,
  validateAcquisitionForm,
  type AcquisitionFormValues
} from "./wishlist-acquire.ts";
import { normalizeWishlistAcquireResponse } from "./wishlist-contract.ts";
// La misma función que trapea el drawer del shell: el diálogo la consume tal cual en lugar de tener una
// segunda trampa que se desincronice. Probarla aquí es probar el contrato real de contención del diálogo.
import { nextFocusIndex } from "../../../components/navigation/nav-config.ts";

// El contrato del alta desde la wishlist (L4) es la única frontera: si estos cuerpos cambian de forma, el
// backend y la UI dejan de entenderse sin que nadie vea un error de compilación.
const validValues: AcquisitionFormValues = {
  platform: "steam",
  acquiredStore: "amazon",
  acquisitionType: "purchase",
  acquiredAt: "",
  removeFromWishlist: true,
  // S6: los defaults conservadores del diálogo viajan como una elección más; ninguno se infiere de la tienda.
  acquiredStoreType: ACQUIRE_STORE_TYPE_DEFAULT,
  acquisitionProvider: ACQUIRE_PROVIDER_DEFAULT
};

test("construye el cuerpo exacto del contrato con la fecha opcional en null", () => {
  const result = validateAcquisitionForm(validValues);
  assert.deepEqual(result, {
    ok: true,
    body: {
      platform: "steam",
      acquiredStore: "amazon",
      acquisitionType: "purchase",
      acquiredAt: null,
      removeFromWishlist: true,
      acquiredStoreType: "unknown",
      acquisitionProvider: "direct"
    }
  });
});

test("plataforma y tienda de adquisición viajan separadas y aceptan slugs de consola", () => {
  // Una clave de Amazon para Steam es el caso real: la plataforma es `steam`, la tienda `amazon`.
  const result = validateAcquisitionForm({ ...validValues, platform: "PS5", acquiredStore: "amazon" });
  assert.ok(result.ok);
  assert.equal(result.body.platform, "ps5");
  assert.equal(result.body.acquiredStore, "amazon");

  const custom = validateAcquisitionForm({ ...validValues, platform: "other", customPlatform: "Coleco ", acquiredStore: "other", customAcquiredStore: "tienda_local" });
  assert.ok(custom.ok);
  assert.equal(custom.body.platform, "coleco");
  assert.equal(custom.body.acquiredStore, "tienda_local");
});

test("una fecha real se envía tal cual y una imposible se rechaza sin inventar la de hoy", () => {
  assert.deepEqual(parseAcquisitionDate("2026-02-28"), { ok: true, value: "2026-02-28" });
  assert.deepEqual(parseAcquisitionDate("2028-02-29"), { ok: true, value: "2028-02-29" });
  assert.deepEqual(parseAcquisitionDate("   "), { ok: true, value: null });
  assert.deepEqual(parseAcquisitionDate(""), { ok: true, value: null });
  for (const invalid of ["2026-02-30", "2026-13-01", "2026-2-3", "28/02/2026", "2026-02-28T00:00:00Z"]) {
    assert.deepEqual(parseAcquisitionDate(invalid), { ok: false }, `debió rechazar ${invalid}`);
  }

  const rejected = validateAcquisitionForm({ ...validValues, acquiredAt: "2026-02-30" });
  assert.ok(!rejected.ok);
  assert.match(rejected.error, /fecha/i);
});

// La API rechaza una fecha futura («una compra no puede haber ocurrido mañana»): el formulario dice lo mismo
// antes de llamar, con la misma fecha de referencia inyectada para que la prueba no dependa del reloj.
test("una fecha futura se rechaza y el día de hoy sigue siendo válido", () => {
  const today = "2026-03-01";

  const todayResult = validateAcquisitionForm({ ...validValues, acquiredAt: today }, today);
  assert.ok(todayResult.ok);
  assert.equal(todayResult.body.acquiredAt, today);

  const past = validateAcquisitionForm({ ...validValues, acquiredAt: "2019-12-31" }, today);
  assert.ok(past.ok);

  const future = validateAcquisitionForm({ ...validValues, acquiredAt: "2026-03-02" }, today);
  assert.ok(!future.ok);
  assert.match(future.error, /futura/i);
});

test("rechaza tipo, plataforma o tienda inválidos con un mensaje que el usuario puede leer", () => {
  for (const acquisitionType of ["trade", "", "PURCHASE-"] as const) {
    const result = validateAcquisitionForm({ ...validValues, acquisitionType });
    assert.ok(!result.ok);
    assert.match(result.error, /tipo/i);
  }

  for (const platform of ["", "no vale!", "  "]) {
    const result = validateAcquisitionForm({ ...validValues, platform });
    assert.ok(!result.ok);
    assert.match(result.error, /plataforma/i);
  }

  for (const acquiredStore of ["", "no vale!", "  "]) {
    const result = validateAcquisitionForm({ ...validValues, acquiredStore });
    assert.ok(!result.ok);
    assert.match(result.error, /tienda/i);
  }

  const missingCustom = validateAcquisitionForm({ ...validValues, platform: "other", customPlatform: "??" });
  assert.ok(!missingCustom.ok);
});

test("removeFromWishlist nunca se infiere: solo un booleano explícito decide el descarte local", () => {
  const coerced = validateAcquisitionForm({ ...validValues, removeFromWishlist: "true" as unknown as boolean });
  assert.ok(!coerced.ok);
  assert.match(coerced.error, /wishlist/i);

  const explicitFalse = validateAcquisitionForm({ ...validValues, removeFromWishlist: false });
  assert.ok(explicitFalse.ok);
  assert.equal(explicitFalse.body.removeFromWishlist, false);

  const subscription = validateAcquisitionForm({ ...validValues, acquisitionType: "subscription" });
  assert.ok(subscription.ok);
  assert.equal(subscription.body.acquisitionType, "subscription");
});

test("normaliza la respuesta solo cuando los cuatro campos cumplen el contrato", () => {
  assert.deepEqual(
    normalizeWishlistAcquireResponse({ outcome: "created", userLibraryId: 12, gameId: 345, dismissed: true }),
    { outcome: "created", userLibraryId: 12, gameId: 345, dismissed: true }
  );
  assert.deepEqual(
    normalizeWishlistAcquireResponse({ outcome: "already_owned", userLibraryId: 12, gameId: 345, dismissed: false }),
    { outcome: "already_owned", userLibraryId: 12, gameId: 345, dismissed: false }
  );

  const rejected = [
    { outcome: "pending", userLibraryId: 12, gameId: 345, dismissed: true },
    { outcome: "created", userLibraryId: 0, gameId: 345, dismissed: true },
    { outcome: "created", userLibraryId: 12, gameId: -1, dismissed: true },
    { outcome: "created", userLibraryId: 12, gameId: 345, dismissed: "true" },
    { outcome: "created", userLibraryId: 12, dismissed: true },
    { outcome: "created", gameId: 345, dismissed: true },
    null,
    []
  ];
  for (const payload of rejected) {
    assert.equal(normalizeWishlistAcquireResponse(payload), null, `debió rechazar ${JSON.stringify(payload)}`);
  }
});

test("el feedback distingue alta nueva, fila ya existente y el descarte local", () => {
  const created = acquisitionFeedback({ outcome: "created", userLibraryId: 1, gameId: 2, dismissed: true });
  const createdRetained = acquisitionFeedback({ outcome: "created", userLibraryId: 1, gameId: 2, dismissed: false });
  const existing = acquisitionFeedback({ outcome: "already_owned", userLibraryId: 1, gameId: 2, dismissed: true });
  const existingRetained = acquisitionFeedback({ outcome: "already_owned", userLibraryId: 1, gameId: 2, dismissed: false });

  assert.equal(created.tone, "success");
  assert.equal(existing.tone, "info");
  assert.match(created.message, /[Aa]ñadido a tu biblioteca/);
  assert.match(created.message, /no volverá/);
  assert.match(createdRetained.message, /[Ss]igue en tu wishlist/);
  assert.doesNotMatch(createdRetained.message, /no volverá/);
  assert.match(existing.message, /[Yy]a estaba en tu biblioteca/);
  assert.match(existing.message, /no se duplicó/);
  assert.match(existing.message, /no volverá/);
  assert.doesNotMatch(existingRetained.message, /no volverá/);
  assert.notEqual(created.message, existing.message);
});

test("el aviso de Steam manda a quitarlo manualmente y promete que el sync no lo readmite", () => {
  // S4: solo aviso. Un texto que sugiera que el servidor toca Steam sería el bug, no la copy.
  assert.match(STEAM_REMOVAL_NOTICE, /Solo aviso/);
  assert.match(STEAM_REMOVAL_NOTICE, /Steam/);
  assert.match(STEAM_REMOVAL_NOTICE, /manualmente/);
  assert.doesNotMatch(STEAM_REMOVAL_NOTICE, /quitamos en Steam|lo quitamos de Steam/i);
  assert.match(STEAM_REMOVAL_SYNC_NOTE, /sincronización/);
  assert.match(STEAM_REMOVAL_SYNC_NOTE, /no volverá/);
});

test("los selectores exponen los cinco tipos y una plataforma por tienda conocida", () => {
  assert.deepEqual(
    ACQUISITION_TYPE_OPTIONS.map((option) => option.value),
    ["purchase", "gift", "free", "bundle", "subscription"]
  );
  for (const option of ACQUISITION_TYPE_OPTIONS) {
    assert.ok(option.label.trim().length > 0);
  }

  const values = ACQUIRE_PLATFORM_OPTIONS.map((option) => option.value);
  for (const store of ["steam", "epic", "gog", "xbox", "amazon", "ubisoft", "humble", "battlenet", "switch", "ps5"]) {
    assert.ok(values.includes(store), `falta ${store}`);
  }
  assert.equal(new Set(values).size, values.length, "no debe repetir plataformas");

  // El selector de «tienda donde lo adquiriste» no ofrece consolas: una tienda es un sitio donde se compra.
  assert.deepEqual(
    ACQUIRE_STORE_OPTIONS.map((option) => option.value),
    ["steam", "epic", "gog", "xbox", "amazon", "ubisoft", "humble", "battlenet"]
  );
});

// El BFF recibe JSON crudo, no un formulario: la misma frontera se valida otra vez, esta vez sin campos
// «other» ni defaults. Un cuerpo incompleto se rechaza en lugar de completarse con lo que el cliente creía.
test("el cuerpo del BFF exige la forma exacta y normaliza sin rellenar defaults", () => {
  assert.deepEqual(
    normalizeAcquireRequestBody({
      platform: " Steam ",
      acquiredStore: "Amazon",
      acquisitionType: "bundle",
      acquiredAt: null,
      removeFromWishlist: false
    }),
    {
      platform: "steam",
      acquiredStore: "amazon",
      acquisitionType: "bundle",
      acquiredAt: null,
      removeFromWishlist: false,
      // Ausentes: el BFF no inventa el default de la UI, los deja en null para un cliente viejo.
      acquiredStoreType: null,
      acquisitionProvider: null
    }
  );

  assert.deepEqual(
    normalizeAcquireRequestBody({
      platform: "ps5",
      acquiredStore: "amazon",
      acquisitionType: "gift",
      acquiredAt: "2026-02-28",
      removeFromWishlist: true,
      acquiredStoreType: "official",
      acquisitionProvider: "itad"
    }),
    {
      platform: "ps5",
      acquiredStore: "amazon",
      acquisitionType: "gift",
      acquiredAt: "2026-02-28",
      removeFromWishlist: true,
      acquiredStoreType: "official",
      acquisitionProvider: "itad"
    }
  );

  const valid = {
    platform: "steam",
    acquiredStore: "steam",
    acquisitionType: "purchase",
    acquiredAt: null,
    removeFromWishlist: true
  };
  const rejected = [
    null,
    [],
    "steam",
    { ...valid, platform: undefined },
    { ...valid, acquiredStore: "" },
    { ...valid, acquisitionType: "trade" },
    { ...valid, acquisitionType: undefined },
    { ...valid, acquiredAt: undefined },
    { ...valid, acquiredAt: "" },
    { ...valid, acquiredAt: "2026-02-30" },
    { ...valid, removeFromWishlist: "true" },
    { ...valid, removeFromWishlist: undefined }
  ];
  for (const payload of rejected) {
    assert.equal(normalizeAcquireRequestBody(payload), null, `debió rechazar ${JSON.stringify(payload)}`);
  }
});

// `aria-modal="true"` es una promesa: el foco no puede salir al fondo de la página. El diálogo decide con
// `nextFocusIndex` (el mismo trípode del drawer), así que los bordes se prueban aquí y el resto lo hace el
// navegador. Si la utilidad cambia de semántica, este test falla y el diálogo deja de mentir.
test("la contención de Tab envuelve en los dos extremos y deja el paso nativo en medio", () => {
  assert.equal(nextFocusIndex(-1, 3, false), 0, "foco en el panel: Tab entra al primer control");
  assert.equal(nextFocusIndex(-1, 3, true), 2, "foco en el panel: Shift+Tab entra al último control");
  assert.equal(nextFocusIndex(2, 3, false), 0, "último control + Tab vuelve al primero");
  assert.equal(nextFocusIndex(0, 3, true), 2, "primer control + Shift+Tab vuelve al último");
  assert.equal(nextFocusIndex(0, 3, false), null, "en medio manda el navegador");
  assert.equal(nextFocusIndex(2, 3, true), null, "en medio manda el navegador (hacia atrás)");
  assert.equal(nextFocusIndex(0, 0, false), null, "sin controles no hay a dónde mover el foco");
});

// S3: el descarte local viene marcado por omisión (el caso mayoritario de quien acaba de comprar), pero
// desmarcarlo es una acción de primera clase y nunca toca Steam. La constante evita que el diálogo y su
// comentario vuelvan a decir cosas distintas, que es justo el defecto que se corrigió aquí.
test("el descarte local viene marcado por omisión y sigue siendo solo local", () => {
  assert.equal(ACQUIRE_REMOVE_DEFAULT, true);
});

// S6/L14: la clasificación de la tienda y el proveedor donde se vio la oferta son dos hechos manuales. La lista
// es cerrada (el backend guarda exactamente estas palabras) y las etiquetas están en español para el usuario.
test("los dos selectores nuevos traen el vocabulario cerrado, en español y con defaults conservadores", () => {
  assert.deepEqual(
    ACQUIRED_STORE_TYPE_OPTIONS.map((option) => option.value),
    ["official", "keyshop", "unknown"]
  );
  assert.deepEqual(
    ACQUIRED_STORE_TYPE_OPTIONS.map((option) => option.label),
    ["Oficial / autorizada", "Keyshop / mercado gris", "No sé"]
  );

  assert.deepEqual(
    ACQUISITION_PROVIDER_OPTIONS.map((option) => option.value),
    ["itad", "ggdeals", "direct", "other"]
  );
  assert.deepEqual(
    ACQUISITION_PROVIDER_OPTIONS.map((option) => option.label),
    ["IsThereAnyDeal", "gg.deals", "Tienda directa", "Otro"]
  );

  // Dos preguntas distintas, no una: los rótulos no pueden repetirse ni confundirse con la plataforma.
  assert.equal(ACQUIRE_STORE_TYPE_LABEL, "Tipo de tienda");
  assert.equal(ACQUIRE_PROVIDER_LABEL, "Oferta encontrada en");
  assert.notEqual(ACQUIRE_STORE_TYPE_LABEL, ACQUIRE_PROVIDER_LABEL);

  // El default no afirma nada sobre la licencia de la tienda y no inventa un proveedor: no sé + tienda directa.
  assert.equal(ACQUIRE_STORE_TYPE_DEFAULT, "unknown");
  assert.equal(ACQUIRE_PROVIDER_DEFAULT, "direct");

  // El ejemplo nombra casos reales y recuerda que la plataforma se pregunta aparte.
  assert.match(ACQUIRE_STORE_TYPE_HINT, /Eneba/);
  assert.match(ACQUIRE_STORE_TYPE_HINT, /Keyshop/);
  assert.match(ACQUIRE_STORE_TYPE_HINT, /gg\.deals/);
  assert.match(ACQUIRE_STORE_TYPE_HINT, /plataforma va aparte/);
});

test("el formulario envía la clasificación y el proveedor elegidos, tal cual", () => {
  const result = validateAcquisitionForm({
    ...validValues,
    acquiredStoreType: "keyshop",
    acquisitionProvider: "ggdeals"
  });
  assert.ok(result.ok);
  assert.equal(result.body.acquiredStoreType, "keyshop");
  assert.equal(result.body.acquisitionProvider, "ggdeals");

  const defaults = validateAcquisitionForm({
    ...validValues,
    acquiredStoreType: ACQUIRE_STORE_TYPE_DEFAULT,
    acquisitionProvider: ACQUIRE_PROVIDER_DEFAULT
  });
  assert.ok(defaults.ok);
  assert.equal(defaults.body.acquiredStoreType, "unknown");
  assert.equal(defaults.body.acquisitionProvider, "direct");
});

test("clasificación y proveedor fuera del vocabulario se rechazan con un mensaje accionable", () => {
  for (const acquiredStoreType of ["grey", "", "OFFICIAL", "keyshop ", "official|keyshop"]) {
    const result = validateAcquisitionForm({ ...validValues, acquiredStoreType });
    assert.ok(!result.ok, `debió rechazar tipo de tienda ${JSON.stringify(acquiredStoreType)}`);
    assert.match(result.error, /tienda/i);
  }

  for (const acquisitionProvider of ["steam", "", "itad,ggdeals", "ITAD"]) {
    const result = validateAcquisitionForm({ ...validValues, acquisitionProvider });
    assert.ok(!result.ok, `debió rechazar proveedor ${JSON.stringify(acquisitionProvider)}`);
    assert.match(result.error, /oferta/i);
  }
});

// S6: «si fue por keyshop oficial o no y que proveedor si itad o ggdeal etc» son dos respuestas independientes.
// ITAD lista tiendas oficiales y keyshops, así que «la vi en ITAD» no puede forzar «oficial»: esas dos
// combinaciones cruzadas tienen que sobrevivir intactas hasta el cuerpo, sin que ninguna reescriba a la otra.
test("la clasificación y el proveedor son independientes: ITAD no fuerza oficial", () => {
  const combinations = [
    { acquiredStoreType: "official", acquisitionProvider: "itad" },
    { acquiredStoreType: "keyshop", acquisitionProvider: "itad" },
    { acquiredStoreType: "keyshop", acquisitionProvider: "direct" },
    { acquiredStoreType: "official", acquisitionProvider: "ggdeals" },
    { acquiredStoreType: "unknown", acquisitionProvider: "other" }
  ] as const;

  for (const combination of combinations) {
    const result = validateAcquisitionForm({ ...validValues, ...combination });
    assert.ok(result.ok, `debió aceptar ${JSON.stringify(combination)}`);
    assert.equal(result.body.acquiredStoreType, combination.acquiredStoreType);
    assert.equal(result.body.acquisitionProvider, combination.acquisitionProvider);
  }

  // La tienda real se conserva: clasificar no reemplaza `acquiredStore` por el proveedor.
  const eneba = validateAcquisitionForm({
    ...validValues,
    acquiredStore: "other",
    customAcquiredStore: "eneba",
    acquiredStoreType: "keyshop",
    acquisitionProvider: "ggdeals"
  });
  assert.ok(eneba.ok);
  assert.equal(eneba.body.acquiredStore, "eneba");
  assert.equal(eneba.body.acquiredStoreType, "keyshop");
  assert.equal(eneba.body.acquisitionProvider, "ggdeals");
});

// L14: el BFF es la frontera de la red, así que también acepta los payloads viejos (sin los campos nuevos, o con
// null explícito) y los reenvía como null: compatibilidad, nunca un default inventado.
test("el BFF acepta payloads viejos sin los campos nuevos y los reenvía como null", () => {
  const oldPayload = {
    platform: "steam",
    acquiredStore: "amazon",
    acquisitionType: "purchase",
    acquiredAt: null,
    removeFromWishlist: true
  };

  assert.deepEqual(normalizeAcquireRequestBody(oldPayload), {
    platform: "steam",
    acquiredStore: "amazon",
    acquisitionType: "purchase",
    acquiredAt: null,
    removeFromWishlist: true,
    acquiredStoreType: null,
    acquisitionProvider: null
  });

  assert.deepEqual(normalizeAcquireRequestBody({ ...oldPayload, acquiredStoreType: null, acquisitionProvider: null }), {
    platform: "steam",
    acquiredStore: "amazon",
    acquisitionType: "purchase",
    acquiredAt: null,
    removeFromWishlist: true,
    acquiredStoreType: null,
    acquisitionProvider: null
  });

  assert.deepEqual(normalizeAcquireRequestBody({ ...oldPayload, acquiredStoreType: "keyshop", acquisitionProvider: "itad" }), {
    platform: "steam",
    acquiredStore: "amazon",
    acquisitionType: "purchase",
    acquiredAt: null,
    removeFromWishlist: true,
    acquiredStoreType: "keyshop",
    acquisitionProvider: "itad"
  });

  // Los inválidos se rechazan enteros: ni se descartan en silencio ni se sustituyen por el default.
  for (const payload of [
    { ...oldPayload, acquiredStoreType: "grey" },
    { ...oldPayload, acquiredStoreType: "" },
    { ...oldPayload, acquisitionProvider: "steam" },
    { ...oldPayload, acquisitionProvider: 4 },
    { ...oldPayload, acquiredStoreType: true }
  ]) {
    assert.equal(normalizeAcquireRequestBody(payload), null, `debió rechazar ${JSON.stringify(payload)}`);
  }
});
