import test from "node:test";
import assert from "node:assert/strict";
import {
  CANDIDATE_QUERY_MAX,
  MAX_TIER_ITEMS,
  normalizeBundlePurchaseDuplicates,
  normalizeBundlePurchaseList,
  normalizeBundlePurchasePreview,
  normalizeBundlePurchaseRequest,
  normalizeBundlePurchaseResult,
  normalizeCatalogCandidates,
  parseCatalogCandidateLimit,
  parseCatalogCandidateQuery
} from "./bundle-purchases.ts";

// S3-S5: contrato de compra de bundles. El backend es la autoridad; este módulo es la frontera que
// valida su forma antes de que un campo raro llegue al render, y la que arma el único cuerpo que el BFF
// reenvía. Todo se prueba con payloads literales: nada de red, nada de reloj.

const HASH = "a".repeat(64);

function item(overrides: Record<string, unknown> = {}) {
  return {
    itemKey: "item-1",
    title: "Juego Uno",
    type: "game",
    priceMinor: 999,
    currency: "USD",
    itadGameId: "itad-uuid-1",
    url: "https://isthereanydeal.com/game/x",
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

function tier(overrides: Record<string, unknown> = {}) {
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

function preview(overrides: Record<string, unknown> = {}) {
  return {
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega Bundle",
    shopId: 12,
    shopName: "Fanatical",
    pageUrl: "https://isthereanydeal.com/bundle/x",
    dealUrl: null,
    details: null,
    publishedAt: "2026-10-01T10:00:00Z",
    expiresAt: "2026-10-20T10:00:00Z",
    observedAt: "2026-10-19T10:00:00Z",
    snapshotHash: HASH,
    purchased: false,
    purchaseId: null,
    tiers: [tier()],
    ...overrides
  };
}

function request(overrides: Record<string, unknown> = {}) {
  return {
    source: "itad",
    bundleKey: "bundle-1",
    tierKey: "tier-1",
    platform: "steam",
    snapshotHash: HASH,
    acquiredAt: "2026-10-20",
    acquiredStore: "fanatical",
    acquiredStoreType: "official",
    acquisitionProvider: "itad",
    items: [{ itemKey: "item-1", platform: null, canonicalGameId: null }],
    ...overrides
  };
}

function result(overrides: Record<string, unknown> = {}) {
  return {
    purchaseId: 9,
    created: true,
    alreadyReported: false,
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega Bundle",
    tierKey: "tier-1",
    platform: "steam",
    snapshotHash: HASH,
    selectedCount: 2,
    importedCount: 1,
    duplicateCount: 1,
    wishlistRemovedCount: 1,
    games: [
      {
        itemKey: "item-1",
        title: "Juego Uno",
        gameId: 501,
        platform: "steam",
        outcome: "imported",
        userLibraryId: 77,
        canonicalSource: "itad",
        wishlistRemoved: true
      }
    ],
    duplicates: [{ anything: "la forma de este arreglo no está en el contrato" }],
    ...overrides
  };
}

// --- snapshot de la vista previa -------------------------------------------

test("el snapshot es 64 hex y se normaliza a minúsculas", () => {
  assert.equal(normalizeBundlePurchasePreview(preview({ snapshotHash: HASH }))?.snapshotHash, HASH);
  const upper = normalizeBundlePurchasePreview(preview({ snapshotHash: "A".repeat(64) }));
  assert.equal(upper?.snapshotHash, "a".repeat(64), "el mismo hash en mayúsculas es el mismo snapshot");
});

test("un snapshot que no es 64 hex invalida la vista previa entera", () => {
  for (const hash of ["", "a".repeat(63), "a".repeat(65), "z".repeat(64), "a".repeat(63) + "-", null, 42]) {
    assert.equal(normalizeBundlePurchasePreview(preview({ snapshotHash: hash })), null, String(hash));
  }
});

test("la vista previa conserva la identidad del bundle y sus fechas ISO", () => {
  const normalized = normalizeBundlePurchasePreview(preview());
  assert.equal(normalized?.source, "itad");
  assert.equal(normalized?.bundleKey, "bundle-1");
  assert.equal(normalized?.shopName, "Fanatical");
  assert.equal(normalized?.shopId, "12", "el backend publica shopId como texto: no se convierte a número");
  assert.equal(normalized?.pageUrl, "https://isthereanydeal.com/bundle/x");
  assert.equal(normalized?.dealUrl, null, "un enlace ausente no se inventa");
  assert.equal(normalized?.expiresAt, "2026-10-20T10:00:00Z");
  assert.equal(normalized?.purchased, false);
  assert.equal(normalized?.purchaseId, null);
});

test("un enlace que no es https absoluto no llega al render", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({ pageUrl: "javascript:alert(1)", dealUrl: "http://isthereanydeal.com/x" })
  );
  assert.equal(normalized?.pageUrl, null);
  assert.equal(normalized?.dealUrl, null);
});

// --- identidad de los ítems (S4) ------------------------------------------

test("un ítem identificado por ITAD no pide vínculo manual", () => {
  const normalized = normalizeBundlePurchasePreview(preview());
  const parsed = normalized?.tiers[0]?.items[0];
  assert.equal(parsed?.itadGameId, "itad-uuid-1");
  assert.equal(parsed?.requiresManualLink, false);
});

test("un ítem sin itadGameId ni canónico pide vínculo manual aunque el servidor diga que no", () => {
  // Nunca se fabrica identidad: sin id de ITAD y sin id canónico no hay nada que el backend pueda
  // resolver, así que la UI tiene que pedir el vínculo antes de confirmar.
  const normalized = normalizeBundlePurchasePreview(
    preview({ tiers: [tier({ items: [item({ itadGameId: null, requiresManualLink: false })] })] })
  );
  assert.equal(normalized?.tiers[0]?.items[0]?.requiresManualLink, true);
});

test("un vínculo manual ya resuelto por el servidor se conserva sin volver a pedirlo", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({
          items: [
            item({
              itadGameId: null,
              canonicalGameId: 501,
              canonicalSource: "manual",
              requiresManualLink: false
            })
          ]
        })
      ]
    })
  );
  const parsed = normalized?.tiers[0]?.items[0];
  assert.equal(parsed?.canonicalGameId, 501);
  assert.equal(parsed?.canonicalSource, "manual");
  assert.equal(parsed?.requiresManualLink, false);
});

test("un ítem identificado por ITAD ignora un vínculo manual contradictorio", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({ tiers: [tier({ items: [item({ requiresManualLink: true })] })] })
  );
  // Con itadGameId presente el cuerpo debe mandar `canonicalGameId: null`: pedir un vínculo aquí
  // llevaría a mandar un id explícito que el contrato prohíbe para ítems identificados.
  assert.equal(normalized?.tiers[0]?.items[0]?.requiresManualLink, false);
});

test("el estado de posesión es de tres valores: sí, no o desconocido", () => {
  const owned = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({
          items: [
            item({ owned: true, ownedUserLibraryId: 77, ownedPlatform: "steam" }),
            item({ itemKey: "item-2", owned: false }),
            item({ itemKey: "item-3", owned: null })
          ]
        })
      ]
    })
  );
  const items = owned?.tiers[0]?.items ?? [];
  assert.equal(items[0]?.owned, true);
  assert.equal(items[0]?.ownedUserLibraryId, 77);
  assert.equal(items[0]?.ownedPlatform, "steam");
  assert.equal(items[1]?.owned, false);
  assert.equal(items[2]?.owned, null);
});

// --- tiers: jamás confirmar lo que no es importable ----------------------

test("un tier importable con motivo de bloqueo se degrada a no importable", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({ tiers: [tier({ importable: true, blockedReason: "addon_tier" })] })
  );
  const parsed = normalized?.tiers[0];
  assert.equal(parsed?.importable, false);
  assert.equal(parsed?.blockedReason, "addon_tier", "el motivo del servidor se conserva");
});

test("un motivo fuera del vocabulario se descarta y un tier no importable no inventa causa", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({ tierKey: "t1", importable: false, blockedReason: "porque_si" }),
        tier({ tierKey: "t2", importable: false, blockedReason: null })
      ]
    })
  );
  assert.equal(normalized?.tiers[0]?.blockedReason, null);
  assert.equal(normalized?.tiers[0]?.importable, false);
  assert.equal(normalized?.tiers[1]?.blockedReason, null);
});

test("un tier de selección sin cupo exacto no se puede confirmar", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({
          selectionMode: "choice",
          selectionCount: null,
          requiredSelectionCount: null,
          items: [item({ itemKey: "a" }), item({ itemKey: "b" })]
        })
      ]
    })
  );
  const parsed = normalized?.tiers[0];
  assert.equal(parsed?.selectionMode, "choice");
  assert.equal(parsed?.requiredSelectionCount, null);
  assert.equal(parsed?.importable, false);
  assert.equal(parsed?.blockedReason, "invalid_selection_count");
});

test("el backend publica 0 cuando el cupo es inválido: ese tier no se confirma", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({ tiers: [tier({ selectionMode: "choice", requiredSelectionCount: 0, selectionCount: 0 })] })
  );
  assert.equal(normalized?.tiers[0]?.requiredSelectionCount, null);
  assert.equal(normalized?.tiers[0]?.importable, false);
  assert.equal(normalized?.tiers[0]?.blockedReason, "invalid_selection_count");
});

test("el cupo exacto se toma de requiredSelectionCount y cae al selectionCount heredado", () => {
  const withRequired = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({
          selectionMode: "choice",
          selectionCount: 2,
          requiredSelectionCount: 1,
          items: [item({ itemKey: "a" }), item({ itemKey: "b" })]
        })
      ]
    })
  );
  assert.equal(withRequired?.tiers[0]?.requiredSelectionCount, 1);

  const legacy = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({
          selectionMode: "choice",
          selectionCount: 2,
          requiredSelectionCount: null,
          items: [item({ itemKey: "a" }), item({ itemKey: "b" })]
        })
      ]
    })
  );
  assert.equal(legacy?.tiers[0]?.requiredSelectionCount, 2);
  assert.equal(legacy?.tiers[0]?.importable, true);
});

test("un cupo exacto mayor que los ítems es irresoluble y bloquea el tier", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({
      tiers: [
        tier({ selectionMode: "choice", requiredSelectionCount: 3, items: [item({ itemKey: "a" })] })
      ]
    })
  );
  assert.equal(normalized?.tiers[0]?.importable, false);
  assert.equal(normalized?.tiers[0]?.blockedReason, "invalid_selection_count");
});

test("un tier sin ningún juego elegible no se confirma", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({
      tiers: [tier({ items: [item({ eligible: false, type: "dlc" })] })]
    })
  );
  assert.equal(normalized?.tiers[0]?.importable, false);
  assert.equal(normalized?.tiers[0]?.blockedReason, "no_eligible_games");
});

test("un modo de selección desconocido no se confirma: no se sabe si hay que elegir juegos", () => {
  const normalized = normalizeBundlePurchasePreview(
    preview({ tiers: [tier({ selectionMode: "mixto" })] })
  );
  assert.equal(normalized?.tiers[0]?.importable, false);
  assert.equal(normalized?.tiers[0]?.blockedReason, "invalid_selection_count");
});

test("los ítems malformados se descartan uno a uno y el tope del contrato se respeta", () => {
  const many = Array.from({ length: MAX_TIER_ITEMS + 5 }, (_, index) =>
    item({ itemKey: `item-${index}`, title: `Juego ${index}` })
  );
  const normalized = normalizeBundlePurchasePreview(
    preview({ tiers: [tier({ items: [item({ itemKey: "" }), ...many] })] })
  );
  const items = normalized?.tiers[0]?.items ?? [];
  assert.equal(items.length, MAX_TIER_ITEMS);
  assert.equal(items[0]?.itemKey, "item-0", "el ítem sin itemKey no entra");
  assert.equal(normalized?.tiers[0]?.items[0]?.title, "Juego 0");
});

// --- cuerpo de la compra: solo lo que el contrato acepta ------------------

test("un cuerpo válido viaja tal cual, con la plataforma como llave canónica", () => {
  const body = normalizeBundlePurchaseRequest(request());
  assert.deepEqual(body, {
    source: "itad",
    bundleKey: "bundle-1",
    tierKey: "tier-1",
    platform: "steam",
    snapshotHash: HASH,
    acquiredAt: "2026-10-20",
    acquiredStore: "fanatical",
    acquiredStoreType: "official",
    acquisitionProvider: "itad",
    items: [{ itemKey: "item-1", platform: null, canonicalGameId: null }]
  });
});

test("la plataforma se exige canónica: el backend conoce tiendas y slugs, pero no se reescribe la intención", () => {
  // StoreKeys.Normalize del backend acepta las tiendas y también slugs de plataforma (una consola), así
  // que aquí se acepta la llave ya canónica y se rechaza cualquier otra grafía.
  assert.equal(normalizeBundlePurchaseRequest(request({ platform: "switch" }))?.platform, "switch");
  for (const platform of ["Steam", "Nintendo Switch", "no vale", "", null, 7]) {
    assert.equal(normalizeBundlePurchaseRequest(request({ platform })), null, String(platform));
  }
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({ items: [{ itemKey: "item-1", platform: "switch", canonicalGameId: null }] })
    )?.items[0]?.platform,
    "switch"
  );
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({ items: [{ itemKey: "item-1", platform: "Nintendo Switch", canonicalGameId: null }] })
    ),
    null
  );
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({ items: [{ itemKey: "item-1", platform: "gog", canonicalGameId: 501 }] })
    )?.items[0]?.platform,
    "gog"
  );
});

test("la tienda de adquisición se normaliza con el mismo punto único de la biblioteca", () => {
  assert.equal(normalizeBundlePurchaseRequest(request({ acquiredStore: "Ubisoft Connect" }))?.acquiredStore, "ubisoft");
  assert.equal(normalizeBundlePurchaseRequest(request({ acquiredStore: "Fanatical" }))?.acquiredStore, "fanatical");
  for (const store of ["", "Humble Bundle", "  ", "a b", null]) {
    assert.equal(normalizeBundlePurchaseRequest(request({ acquiredStore: store })), null, String(store));
  }
});

test("los vocabularios de tipo de tienda y proveedor son cerrados", () => {
  assert.equal(normalizeBundlePurchaseRequest(request({ acquiredStoreType: "gris" })), null);
  assert.equal(normalizeBundlePurchaseRequest(request({ acquisitionProvider: "itad_o_algo" })), null);
  assert.equal(normalizeBundlePurchaseRequest(request({ acquiredStoreType: undefined })), null);
  assert.equal(normalizeBundlePurchaseRequest(request({ acquisitionProvider: null })), null);
  assert.equal(
    normalizeBundlePurchaseRequest(request({ acquiredStoreType: "keyshop", acquisitionProvider: "ggdeals" }))
      ?.acquisitionProvider,
    "ggdeals"
  );
});

test("la fecha de compra es un día real del calendario", () => {
  for (const date of ["2026-02-30", "", "2026-1-1", "2026-10-20T00:00:00Z", null, 20261020]) {
    assert.equal(normalizeBundlePurchaseRequest(request({ acquiredAt: date })), null, String(date));
  }
  assert.equal(normalizeBundlePurchaseRequest(request({ acquiredAt: "2026-02-28" }))?.acquiredAt, "2026-02-28");
});

test("los identificadores no admiten espacios ni texto libre", () => {
  for (const key of ["", "  ", "bundle 1", "bundle/1", "a".repeat(129), null]) {
    assert.equal(normalizeBundlePurchaseRequest(request({ bundleKey: key })), null, String(key));
  }
  assert.equal(normalizeBundlePurchaseRequest(request({ tierKey: "tier:1" }))?.tierKey, "tier:1");
});

test("la selección de ítems no puede ir vacía, duplicada ni con ids inválidos", () => {
  assert.equal(normalizeBundlePurchaseRequest(request({ items: [] })), null);
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({
        items: [
          { itemKey: "item-1", platform: null, canonicalGameId: null },
          { itemKey: "item-1", platform: null, canonicalGameId: null }
        ]
      })
    ),
    null
  );
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({ items: [{ itemKey: "item-1", platform: null, canonicalGameId: -1 }] })
    ),
    null
  );
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({ items: [{ itemKey: "item-1", platform: null, canonicalGameId: 0 }] })
    ),
    null
  );
  assert.equal(
    normalizeBundlePurchaseRequest(
      request({ items: [{ itemKey: "item-1", platform: null, canonicalGameId: "501" }] })
    ),
    null,
    "un id canónico no se convierte desde texto"
  );
});

test("un cuerpo que no es objeto o sin el snapshot no pasa", () => {
  for (const payload of [null, [], "{}", 42, request({ snapshotHash: "abc" })]) {
    assert.equal(normalizeBundlePurchaseRequest(payload), null);
  }
});

// --- resultado de la compra (S3/S5) --------------------------------------

test("el resultado conserva los conteos y los juegos del servidor", () => {
  const normalized = normalizeBundlePurchaseResult(result());
  assert.equal(normalized?.purchaseId, 9);
  assert.equal(normalized?.created, true);
  assert.equal(normalized?.alreadyReported, false);
  assert.equal(normalized?.selectedCount, 2);
  assert.equal(normalized?.importedCount, 1);
  assert.equal(normalized?.duplicateCount, 1);
  assert.equal(normalized?.wishlistRemovedCount, 1);
  assert.equal(normalized?.games.length, 1);
  assert.equal(normalized?.games[0]?.outcome, "imported");
  assert.equal(normalized?.games[0]?.userLibraryId, 77);
  assert.equal(normalized?.games[0]?.wishlistRemoved, true);
});

test("el arreglo `duplicates` del resultado no se interpreta: su forma no está en el contrato", () => {
  const normalized = normalizeBundlePurchaseResult(result());
  assert.equal("duplicates" in (normalized ?? {}), false, "solo se usan los conteos publicados");
});

test("un juego del resultado sin título o con plataforma ajena se descarta", () => {
  const normalized = normalizeBundlePurchaseResult(
    result({
      games: [
        { itemKey: "a", title: "Bueno", gameId: 1, platform: "steam", outcome: "imported", userLibraryId: 1 },
        { itemKey: "b", title: "", platform: "steam", outcome: "imported" },
        { itemKey: "c", title: "Sin plataforma", platform: "", outcome: "imported" }
      ]
    })
  );
  assert.equal(normalized?.games.length, 1);
  assert.equal(normalized?.games[0]?.title, "Bueno");
});

test("un resultado sin los conteos no inventa importaciones", () => {
  const normalized = normalizeBundlePurchaseResult({
    purchaseId: 9,
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega",
    tierKey: "tier-1",
    platform: "steam",
    snapshotHash: HASH,
    games: []
  });
  assert.equal(normalized?.importedCount, 0);
  assert.equal(normalized?.duplicateCount, 0);
  assert.equal(normalized?.wishlistRemovedCount, 0);
  assert.equal(normalized?.created, false);
  assert.equal(normalized?.alreadyReported, false);
});

test("un resultado con purchaseId inválido es una respuesta inválida, no una compra", () => {
  assert.equal(normalizeBundlePurchaseResult(result({ purchaseId: 0 })), null);
  assert.equal(normalizeBundlePurchaseResult(result({ purchaseId: "9" })), null);
  assert.equal(normalizeBundlePurchaseResult(result({ snapshotHash: "abc" })), null);
});

// --- historial y keys duplicadas -----------------------------------------

test("el historial cuenta los juegos del arreglo sin inventar su forma", () => {
  const list = normalizeBundlePurchaseList({
    items: [
      {
        purchaseId: 9,
        source: "itad",
        bundleKey: "bundle-1",
        title: "Mega Bundle",
        platform: "steam",
        acquiredAt: "2026-10-20",
        importedCount: 2,
        duplicateCount: 1,
        games: [{}, {}, {}]
      }
    ],
    count: 1
  });
  assert.ok(list !== null, "el historial válido debe normalizar");
  assert.equal(list.items.length, 1);
  assert.equal(list.items[0]?.gamesCount, 3);
  assert.equal(list.items[0]?.acquiredAt, "2026-10-20");
  assert.equal(list.count, 1);
});

test("una compra sin fecha declarada sigue siendo una compra", () => {
  const list = normalizeBundlePurchaseList({
    items: [
      {
        purchaseId: 10,
        source: "itad",
        bundleKey: "bundle-2",
        title: "Bundle sin fecha",
        platform: "steam",
        acquiredAt: null,
        importedCount: 1,
        duplicateCount: 0,
        games: []
      }
    ]
  });
  assert.ok(list !== null);
  assert.equal(list.items[0]?.acquiredAt, null, "null es un dato ausente, no una fila inválida");
});

test("una entrada inválida invalida el historial entero: una compra no se esconde", () => {
  assert.equal(
    normalizeBundlePurchaseList({
      items: [
        { purchaseId: 0, source: "itad", bundleKey: "b", title: "x", platform: "steam", acquiredAt: "2026-10-20" },
        { purchaseId: 1, source: "itad", bundleKey: "b", title: "x", platform: "steam", acquiredAt: "20/10/2026" }
      ]
    }),
    null
  );
  assert.equal(normalizeBundlePurchaseList(null), null);
  assert.equal(normalizeBundlePurchaseList({ items: "nada" }), null);
});

test("las keys duplicadas son juegos, con la ficha que ya existía en la biblioteca", () => {
  const row = {
    purchaseId: 9,
    source: "itad",
    bundleKey: "bundle-1",
    bundleTitle: "Mega Bundle",
    acquiredAt: "2026-10-20",
    itemKey: "item-1",
    title: "Juego Uno",
    gameId: 501,
    platform: "steam",
    userLibraryId: 77,
    existingTitle: "Juego Uno (2019)"
  };
  const duplicates = normalizeBundlePurchaseDuplicates({ items: [row], count: 7 });
  assert.equal(duplicates?.items.length, 1);
  assert.equal(duplicates?.items[0]?.existingTitle, "Juego Uno (2019)");
  assert.equal(duplicates?.count, 1, "se cuentan las filas pintables, no un total que no se ve");

  // Una fila sin juego o sin plataforma no es un duplicado legible: la lista se rechaza entera en vez de
  // ocultar duplicados parcialmente.
  assert.equal(normalizeBundlePurchaseDuplicates({ items: [{ ...row, title: "" }] }), null);
  assert.equal(normalizeBundlePurchaseDuplicates({ items: [{ ...row, platform: "no vale" }] }), null);

  // El DTO publica `userLibraryId`, `acquiredAt` y `existingTitle` como nullable: un duplicado sin título
  // previo o sin fecha es un dato ausente, no un duplicado falso.
  const sparse = normalizeBundlePurchaseDuplicates({
    items: [{ ...row, userLibraryId: null, acquiredAt: null, existingTitle: null }]
  });
  assert.ok(sparse !== null);
  assert.equal(sparse.items[0]?.existingTitle, null);
  assert.equal(sparse.items[0]?.acquiredAt, null);
  // Una fecha que llega corrupta sí invalida la fila.
  assert.equal(normalizeBundlePurchaseDuplicates({ items: [{ ...row, acquiredAt: "20/10/2026" }] }), null);
});

// --- candidatos del catálogo --------------------------------------------

test("los candidatos son solo juegos del catálogo, con su año y si ya están en biblioteca", () => {
  const candidates = normalizeCatalogCandidates({
    items: [
      { gameId: 501, title: "Juego Uno", releaseYear: 2019, imageUrl: "https://cdn/x.jpg", inLibrary: true },
      { gameId: 0, title: "Sin id" },
      { gameId: 502, title: "" }
    ],
    count: 3
  });
  assert.equal(candidates.items.length, 1);
  assert.equal(candidates.items[0]?.releaseYear, 2019);
  assert.equal(candidates.items[0]?.inLibrary, true);
  assert.equal(candidates.count, 1);
});

test("la búsqueda exige texto útil y acota el límite del servidor", () => {
  assert.equal(parseCatalogCandidateQuery("  floppy  "), "floppy");
  assert.equal(parseCatalogCandidateQuery(" a "), null);
  assert.equal(parseCatalogCandidateQuery(""), null);
  assert.equal(parseCatalogCandidateQuery("x".repeat(CANDIDATE_QUERY_MAX + 1)), null);
  assert.equal(parseCatalogCandidateQuery(null), null);

  assert.equal(parseCatalogCandidateLimit("10"), 10);
  assert.equal(parseCatalogCandidateLimit("1"), 1);
  assert.equal(parseCatalogCandidateLimit("11"), null);
  assert.equal(parseCatalogCandidateLimit("0"), null);
  assert.equal(parseCatalogCandidateLimit("abc"), null);
});
