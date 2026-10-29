import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

// No hay runner de React ni navegador en este repo (`node --test` y nada más), así que las reglas de la
// compra de bundles que sí son deterministas se fijan sobre el contrato de la fuente: quién autentica,
// quién valida, quién escribe, qué se reenvía y qué no puede existir en el render (HTML crudo, códigos,
// userId del cliente). El comportamiento puro está en `bundle-purchases.test.ts` y
// `bundle-purchase-selection.test.mjs`; lo que este archivo NO puede probar es el pintado en el navegador:
// eso queda declarado como no medido.

const ROOT = new URL("../", import.meta.url);

function read(path) {
  return readFile(new URL(path, ROOT), "utf8");
}

const FILES = {
  purchases: "app/api/bff/bundles/purchases/route.ts",
  preview: "app/api/bff/bundles/purchases/preview/route.ts",
  duplicates: "app/api/bff/bundles/purchases/duplicates/route.ts",
  candidates: "app/api/bff/bundles/catalog-candidates/route.ts",
  api: "app/bundles/_lib/bundle-purchases-api.ts",
  dialog: "app/bundles/_components/bundle-purchase-dialog.tsx",
  linkSearch: "app/bundles/_components/bundle-purchase-link-search.tsx",
  context: "app/bundles/_components/bundle-purchase-context.tsx",
  history: "app/bundles/_components/bundle-collection-history.tsx",
  duplicatesList: "app/bundles/_components/bundle-collection-duplicates.tsx",
  client: "app/bundles/bundles-client.tsx",
  rules: "app/bundles/_lib/bundle-purchase.ts",
  contract: "lib/contracts/bundle-purchases.ts"
};

test("los tres endpoints de compras exigen sesión y nunca aceptan un usuario del cliente", async () => {
  const purchases = await read(FILES.purchases);
  // GET y POST: cada uno empieza por la sesión.
  assert.equal(purchases.match(/if \(!session\) return unauthorized\(request\);/g)?.length, 2);
  assert.match(purchases, /getServerSession\(\)/);
  assert.doesNotMatch(purchases, /\buserId\b|\bappUserId\b/, "la identidad viene del token, no del cuerpo");

  const preview = await read(FILES.preview);
  assert.match(preview, /if \(!session\) return unauthorized\(request\);/);
  const duplicates = await read(FILES.duplicates);
  assert.match(duplicates, /if \(!session\) return unauthorized\(request\);/);
  const candidates = await read(FILES.candidates);
  assert.match(candidates, /if \(!session\) return unauthorized\(request\);/);
});

test("el BFF refresca el token, reinstala la cookie y valida el cuerpo antes de llamar", async () => {
  const purchases = await read(FILES.purchases);
  assert.match(purchases, /fetchApiWithAutoRefresh\(/);
  assert.match(purchases, /attachSessionCookie\(/);
  // Una respuesta sin `snapshotHash` o sin `purchaseId` no se reenvía como éxito.
  assert.match(purchases, /normalizeBundlePurchaseRequest\(payload\)/);
  assert.match(purchases, /normalizeBundlePurchaseResult\(/);
  assert.match(purchases, /normalizeBundlePurchaseList\(/);
  assert.match(purchases, /badRequest\(request/);
  assert.match(purchases, /MAX_BODY_BYTES/);
  // 400, 404 y 409 tienen significado: se conserva el status y se prefiere el mensaje del servidor.
  assert.match(purchases, /status === 409/);
  assert.match(purchases, /status === 404/);
  assert.match(purchases, /response\.status/);

  for (const file of [FILES.preview, FILES.duplicates, FILES.candidates]) {
    const source = await read(file);
    assert.match(source, /fetchApiWithAutoRefresh\(/, `${file} debe llamar con refresco de token`);
    assert.match(source, /attachSessionCookie\(/, `${file} debe reinstalar la cookie rotada`);
  }
});

test("la vista previa exige referencias válidas y no deja pasar una plataforma inventada", async () => {
  const preview = await read(FILES.preview);
  assert.match(preview, /parseBundleIdentifier\(incoming\.get\("source"\)\)/);
  assert.match(preview, /parseBundleIdentifier\(incoming\.get\("bundleKey"\)\)/);
  assert.match(preview, /normalizeStore\(rawPlatform\)/);
  assert.match(preview, /platform !== rawPlatform/, "la llave debe llegar canónica, no reescrita");
  assert.match(preview, /badRequest\(request, "La plataforma de la vista previa/);
  assert.match(preview, /normalizeBundlePurchasePreview\(/);
});

test("los candidatos acotan la búsqueda y el límite antes de reenviar", async () => {
  const candidates = await read(FILES.candidates);
  assert.match(candidates, /parseCatalogCandidateQuery\(incoming\.get\("q"\)\)/);
  assert.match(candidates, /parseCatalogCandidateLimit\(incoming\.get\("limit"\)\)/);
  assert.match(candidates, /CANDIDATE_LIMIT_DEFAULT/);
  assert.match(candidates, /normalizeCatalogCandidates\(/);
  assert.match(candidates, /badRequest\(request/);
});

test("toda mutación del cliente pasa por csrfFetch y toda respuesta por el contrato", async () => {
  const api = await read(FILES.api);
  assert.equal(api.match(/csrfFetch\(/g)?.length, 5, "las cinco llamadas usan el helper con CSRF");
  assert.match(api, /method: "POST"/);
  assert.match(api, /parseApiError\(/);
  for (const normalizer of [
    "normalizeBundlePurchasePreview",
    "normalizeBundlePurchaseResult",
    "normalizeBundlePurchaseList",
    "normalizeBundlePurchaseDuplicates",
    "normalizeCatalogCandidates"
  ]) {
    assert.match(api, new RegExp(`${normalizer}\\(`), `${normalizer} debe validar la respuesta`);
  }
  assert.doesNotMatch(api, /\buserId\b/, "ningún llamador manda identidad");
});

test("el diálogo no admite dos envíos, reintenta errores y se re-vista previa si el snapshot viejo", async () => {
  const dialog = await read(FILES.dialog);
  // Doble clic: el ref corta el segundo envío antes de que React re-renderice el botón.
  assert.match(dialog, /if \(busyRef\.current\) return;/);
  assert.match(dialog, /busyRef\.current = true;/);
  assert.match(dialog, /busyRef\.current = false;/);
  assert.match(dialog, /loading=\{busy\}/);
  assert.match(dialog, /disabled=\{busy\}/);

  // 400 ⇒ vista previa otra vez; 404 y 409 se explican con su salida.
  assert.match(dialog, /cause instanceof ApiError && cause\.status === 400/);
  assert.match(dialog, /await loadPreview\(platform\)/);
  assert.match(dialog, /cause\.status === 404/);
  assert.match(dialog, /cause\.status === 409/);
  assert.match(dialog, /Reintentar/);

  // Un solo envío a la vez también en el copy de estado.
  assert.match(dialog, /Guardando la compra y los juegos/);
  // El servidor vuelve a validar; aquí se valida antes para no gastar la llamada.
  assert.match(dialog, /validatePurchaseConfirm\(\{/);
});

test("el diálogo selecciona tier y plataforma con override por juego y vínculo manual", async () => {
  const dialog = await read(FILES.dialog);
  assert.match(dialog, /name="bundle-purchase-tier"/);
  assert.match(dialog, /type="radio"/);
  assert.match(dialog, /Plataforma de la compra/);
  assert.match(dialog, /`Plataforma de «\$\{item\.title\}»`/, "cada juego importado puede overridear la plataforma");
  assert.match(dialog, /<option value="">Igual que la compra/);
  assert.match(dialog, /Tienda donde lo compraste/);
  assert.match(dialog, /Tipo de tienda/);
  assert.match(dialog, /Oferta encontrada en/);
  assert.match(dialog, /type="date"/);
  assert.match(dialog, /Vincular con el catálogo/);
  assert.match(dialog, /BundlePurchaseLinkSearch/);
  // El buscador se monta por ítem: la respuesta de un ítem anterior no puede pisar el texto del actual.
  assert.match(dialog, /<BundlePurchaseLinkSearch\s+key=\{item\.itemKey\}/);
  // Un tier no importable no se puede elegir ni confirmar.
  assert.match(dialog, /disabled=\{!tier\.importable \|\| busy\}/);
  assert.match(dialog, /TIER_BLOCKED_REASON_LABELS/);
  assert.match(dialog, /loading \|\| activeTier === null \|\| !activeTier\.importable/);
});

test("el buscador de fichas aborta, numera la petición y no elige por título", async () => {
  const search = await read(FILES.linkSearch);
  assert.match(search, /new AbortController\(\)/);
  assert.match(search, /controller\.abort\(\)/);
  assert.match(search, /const request = \+\+seq\.current;/);
  assert.match(search, /if \(request !== seq\.current\) return;/);
  assert.match(search, /const text = query\.trim\(\);/);
  assert.match(search, /text\.length < 2/);
  // Elegir es un clic explícito: la única llamada a `onPick` vive en el `onClick` del resultado.
  assert.equal(search.match(/onPick\(/g)?.length, 1, "solo el clic elige la ficha");
  assert.match(search, /onClick=\{\(\) => onPick\(candidate\)\}/);
});

test("las secciones de historial y keys duplicadas usan los endpoints del usuario y se refrescan", async () => {
  const history = await read(FILES.history);
  assert.match(history, /getBundlePurchases\(/);
  assert.match(history, /refreshToken/);
  assert.match(history, /Reintentar/);
  assert.match(history, /Historial de bundles comprados/);

  const duplicates = await read(FILES.duplicatesList);
  assert.match(duplicates, /getBundlePurchaseDuplicates\(/);
  assert.match(duplicates, /refreshToken/);
  assert.match(duplicates, /Keys duplicadas/);
  assert.match(duplicates, /no se guardan códigos de activación/);
  assert.match(duplicates, /existingTitle/);
});

test("la página recarga la página 1 tras comprar y monta las secciones en pestañas accesibles", async () => {
  const client = await read(FILES.client);
  assert.match(client, /setRefreshToken\(\(current\) => current \+ 1\)/);
  assert.match(client, /void load\(1, "replace"\)/, "el bundle comprado sale de pendientes y los conteos bajan");
  assert.match(client, /role="tablist"/);
  assert.match(client, /role="tab"/);
  assert.match(client, /aria-selected=\{activeTab === tab\.id\}/);
  for (const id of ["pending", "history", "duplicates"]) {
    assert.match(client, new RegExp(`id="bundles-panel-${id}"`));
    assert.match(client, new RegExp(`hidden=\\{activeTab !== "${id}"\\}`));
  }
  assert.match(client, /\{historyVisited \? \(/);
  assert.match(client, /\{duplicatesVisited \? \(/);
  assert.match(client, /BundlePurchaseDialog/);
  // El reloj compartido y el orden del servidor siguen intactos (sus propias pruebas lo fijan también).
  assert.match(client, /const EXPIRY_CLOCK_INTERVAL_MS = 60_000;/);
  assert.doesNotMatch(client, /\.sort\(|\.toSorted\(|\.reverse\(/);
});

test("ningún archivo nuevo pinta HTML crudo ni maneja códigos de activación", async () => {
  for (const file of [
    FILES.dialog,
    FILES.linkSearch,
    FILES.context,
    FILES.history,
    FILES.duplicatesList
  ]) {
    const source = await read(file);
    assert.doesNotMatch(source, /dangerouslySetInnerHTML/, `${file} no debe inyectar HTML crudo`);
    assert.doesNotMatch(source, /innerHTML\s*=/, `${file} no debe escribir innerHTML`);
    assert.doesNotMatch(source, /activationCode|redeemCode|\bkeyCode\b/, `${file} no maneja códigos`);
  }
});

test("el módulo de reglas no fabrica identidad ni elige juegos por título", async () => {
  const rules = await read(FILES.rules);
  // Un ítem identificado por ITAD manda null; solo un vínculo manual manda id.
  assert.match(rules, /item\.itadGameId !== null \? null : \(input\.manualLinks\[itemKey\]\?\.gameId \?\? item\.canonicalGameId\)/);
  // La membresía de un tier fijo sale del servidor, no de la selección de la UI.
  assert.match(rules, /eligibleItems\(tier\)\.map\(\(item\) => item\.itemKey\)/);
  // El cupo exacto se compara con requiredSelectionCount.
  assert.match(rules, /selectedKeys\.length !== required/);
  // El default de plataforma no se deduce del shop.
  assert.match(rules, /export const PURCHASE_PLATFORM_DEFAULT = "steam";/);
  assert.match(rules, /Qued[a-záéíóú]* registrad[oa] en Keys duplicadas|Keys duplicadas/);
});

test("el contrato publicado no se inventa: duplicados y juegos del historial son conteos", async () => {
  const contract = await read(FILES.contract);
  // El arreglo `duplicates` del resultado existe, pero la UI no lo duplica: los conteos publicados mandan.
  assert.match(contract, /conteos publicados mandan/);
  assert.match(contract, /gamesCount: Array\.isArray\(rawGames\) \? rawGames\.length : 0/);
  assert.match(contract, /SNAPSHOT_HASH_PATTERN/);
  assert.match(contract, /MAX_PURCHASE_CANDIDATES = 10/);
});

test("los campos nullable del DTO se conservan como ausentes y no invalidan la fila", async () => {
  const contract = await read(FILES.contract);
  // `shopId` es texto, `acquiredAt` es DateOnly? y `existingTitle`/`userLibraryId` son nullable.
  assert.match(contract, /readonly shopId: string \| null;/);
  assert.match(contract, /readonly acquiredAt: string \| null;/);
  assert.match(contract, /readonly existingTitle: string \| null;/);
  assert.match(contract, /readonly userLibraryId: number \| null;/);
  assert.match(contract, /una fecha que llega corrupta sí invalida la fila/i);
});

test("el 400 del servidor se muestra: no se reintenta a ciegas contra un ítem sin título", async () => {
  const dialog = await read(FILES.dialog);
  assert.match(dialog, /const reason = cause\.message\?\.trim\(\);/);
  assert.match(dialog, /reason\n\s*\? `\$\{reason\} Actualizamos la vista previa/);
});
