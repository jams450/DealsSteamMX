import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

// No hay runner de React en este repo (`node --test` y nada más), así que las dos reglas de UI que sí
// son deterministas se prueban sobre el contrato de la fuente: el `details` de ITAD se pinta como ancla
// real con destino/rel seguros y la URL exacta, y el tier suma las filas USD/MXN con ≈ en la conversión.
// Lo que este archivo NO puede probar es el pintado en el navegador: eso queda declarado como no medido.

const read = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

const CLIENT = "app/bundles/bundles-client.tsx";

function region(source, startMarker) {
  const start = source.indexOf(startMarker);
  assert.notEqual(start, -1, `no se encontró «${startMarker}» en ${CLIENT}`);
  const rest = source.slice(start + startMarker.length);
  const end = rest.indexOf("\nfunction ");
  return rest.slice(0, end === -1 ? rest.length : end);
}

test("la URL de details se usa en el título y no se repite debajo", async () => {
  const source = await read(CLIENT);
  const details = region(source, "function BundleDetails(");

  assert.match(details, /item\.detailsUrl\) return null/);
  assert.doesNotMatch(details, /<a\b/);
  const card = region(source, "function BundleCard(");
  assert.match(card, /item\.dealUrl \?\? item\.pageUrl \?\? item\.detailsUrl/);
  assert.match(card, /focus-visible:ring-2/);
  assert.match(card, /se abre en una pestaña nueva/);
});

test("un details que no es URL conserva el texto plano y no inventa un enlace", async () => {
  const source = await read(CLIENT);
  const details = region(source, "function BundleDetails(");

  // La rama de texto es la que no tiene ancla: el texto del proveedor se pinta tal cual.
  assert.match(details, /item\.detailsUrl\) return null/);
  assert.match(details, /\{item\.details\}/);
  assert.equal(/dangerouslySetInnerHTML/.test(source), false, "nada de HTML del proveedor inyectado");
  assert.equal(/linkify/i.test(source), false, "nada de linkificado genérico sobre texto libre");
});

test("el enlace de afiliado del título no cambia", async () => {
  const source = await read(CLIENT);
  const card = region(source, "function BundleCard(");

  assert.match(card, /const href = item\.dealUrl \?\? item\.pageUrl \?\? item\.detailsUrl;/);
  assert.match(card, /href=\{href\}/);
  assert.match(card, /target="_blank"/);
  assert.match(card, /rel="noopener noreferrer"/);
  assert.match(card, /\{item\.title\}/);
});

test("el tier conserva el precio nativo y suma las filas USD/MXN con ≈", async () => {
  const source = await read(CLIENT);
  const tierRow = region(source, "function TierRow(");

  // Diseño original intacto: el precio nativo con su moneda y sus motivos de reparto.
  assert.match(tierRow, /formatMinor\(tier\.priceMinor, tier\.currency\)/);
  assert.match(tierRow, /tier\.currency/, "la moneda nativa se sigue mostrando");
  assert.match(tierRow, /ALLOCATION_REASON_LABELS/);

  // Filas duales y reparto convertido, siempre desde el módulo puro.
  assert.match(source, /from "\.\/_lib\/bundle-money"/);
  assert.match(tierRow, /tierMoneyView\(tier\)/);
  assert.match(tierRow, /allocatedPerGameMoneyView\(tier\)/);
  // La dirección de la tasa depende de la moneda nativa del tier: la llamada tiene que pasarla.
  assert.match(tierRow, /fxDisclosureText\(view\?\.fx \?\? null, view\?\.nativeCurrency \?\? null\)/);
  assert.match(source, /≈/, "la conversión se marca con ≈");
  const dualRows = region(source, "function DualMoneyRows(");
  assert.match(dualRows, /leg\.approximate \? "≈ "/, "la conversión lleva ≈ en la propia cifra");
  assert.match(dualRows, /no disponible/, "una moneda sin tasa se dice, no se pinta como 0");
  assert.match(dualRows, /view\.displayLegs/, "la fila nativa no se duplica");
});

test("la tarjeta de bundles no hace aritmética de tipo de cambio", async () => {
  const source = await read(CLIENT);
  // Ningún multiplicador por una tasa ni lectura de `fxRate` para recalcular cantidades.
  assert.equal(/fxRate\s*\*/.test(source), false);
  assert.equal(/\*\s*[a-zA-Z.]*fxRate/.test(source), false);
  assert.equal(/\*/.test(region(source, "function DualMoneyRows(")), false);
});
