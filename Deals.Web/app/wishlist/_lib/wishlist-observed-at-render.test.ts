import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

// Lectura estática del componente, misma práctica que `lib/contracts/catalog-freshness.test.ts`: la tarjeta
// es un client component con React y `node --test` no lo monta. Lo que se fija aquí es el emparejamiento
// —el distintivo que acompaña a un precio se calcula con la fecha de ESE precio— que es justo lo que se
// rompió antes en la página del juego pintando el precio de Steam con la etiqueta de otra tienda.
const CLIENT = new URL("../wishlist-client.tsx", import.meta.url);

async function client() {
  return readFile(CLIENT, "utf8");
}

// El tipo de las props deja un `}` en la columna 0 y corta cualquier corte ingenuo del bloque; se delimita
// por el componente siguiente.
function section(source: string, start: string, end: string): string {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from);
  assert.ok(from >= 0 && to > from, `no se encontró el bloque ${start} … ${end}`);
  return source.slice(from, to);
}

test("cada banda pinta su fecha observada en el mismo elemento que su importe", async () => {
  const source = await client();

  assert.match(
    source,
    /<BandPriceFact label="Mín\. oficial" amountMinor=\{item\.bestOfficialMinor\} currency=\{MXN\} observedAt=\{item\.bestOfficialObservedAt\} nowMs=\{clockMs\} \/>/
  );
  assert.match(
    source,
    /<BandPriceFact label="Mín\. keys" amountMinor=\{item\.bestKeyshopMinor\} currency=\{MXN\} observedAt=\{item\.bestKeyshopObservedAt\} nowMs=\{clockMs\} \/>/
  );
  // Dos precios, dos llamadas: ni una banda sin distintivo ni un tercer precio inventado.
  assert.equal(source.match(/<BandPriceFact /g)?.length, 2);
});

test("la fecha sale del precio ganador y no de un sello de proveedor ni del refresco", async () => {
  const source = await client();

  // El cliente no busca la fecha en otro sitio: usa exactamente los dos campos aditivos del item.
  assert.equal(source.match(/bestOfficialObservedAt/g)?.length, 1);
  assert.equal(source.match(/bestKeyshopObservedAt/g)?.length, 1);
  assert.doesNotMatch(
    source,
    /observedAt=\{[^}]*(steamSyncedAt|itadSyncedAt|ggDealsSyncedAt|epicSyncedAt|microsoftSyncedAt|refreshedAt)/
  );
  // Sin llamadas nuevas: la fecha llega en el mismo item de la wishlist que ya se pidió.
  assert.doesNotMatch(source, /\/api\/bff\/wishlist\/[a-z-]*observed/);
});

test("sin precio no hay distintivo, y con precio pero sin fecha se dice desconocida", async () => {
  const source = await client();

  const badge = section(source, "function ObservedAtBadge", "function MobileMetrics");
  // La ausencia de precio es lo único que apaga el distintivo; una fecha ilegible no lo apaga.
  assert.match(badge, /if \(amountMinor === null\) return null;/);
  assert.match(badge, /observedAtBadge\(observedAt, /);
  // El texto viene del helper puro, nunca de un literal por estado en el JSX.
  assert.match(badge, /\{badge\.label\}/);
  assert.doesNotMatch(source, /≤72 h|\+72 h/);
  assert.doesNotMatch(source, /"Fecha desconocida"/);
});

test("el distintivo muestra la fecha completa en el tooltip y para lectores de pantalla", async () => {
  const source = await client();

  const badge = section(source, "function ObservedAtBadge", "function MobileMetrics");
  // Tooltip nativo con la fecha y hora completas, no la etiqueta corta.
  assert.match(badge, /title=\{/);
  // El texto corto no se anuncia solo: el lector de pantalla recibe la frase con la fecha completa.
  assert.match(badge, /className="sr-only"/);
  assert.match(badge, /Precio observado/);
  assert.match(badge, /formatFullDateTime\(/);
  // Fecha **y hora**, no solo el día: el tooltip tiene que decir cuándo se leyó el precio.
  assert.match(source, /dateStyle: "medium", timeStyle: "short"/);
});

test("el distintivo se coloca junto al importe con la forma compacta de la tabla", async () => {
  const source = await client();

  const fact = section(source, "function BandPriceFact", "function ObservedAtBadge");
  // Misma estructura que `PriceFact` (etiqueta arriba, importe abajo) más el distintivo en la misma línea.
  assert.match(fact, /<p className="text-xs text-muted">\{label\}<\/p>/);
  assert.match(fact, /<PriceValue amountMinor=\{amountMinor\} currency=\{currency\} \/>/);
  assert.match(fact, /<ObservedAtBadge amountMinor=\{amountMinor\} observedAt=\{observedAt\} nowMs=\{nowMs\} \/>/);
});

// --- Reloj de la página: sin `Date.now()` de módulo y con un solo temporizador compartido.

test("no hay reloj congelado en el módulo: el «ahora» no se calcula ni al cargar ni en el render", async () => {
  const source = await client();

  // Un `Date.now()` de módulo se evalúa una vez por instancia: el proceso del servidor la congela al arrancar y
  // el cliente la congela en otro instante. Ni paridad de hidratación ni antigüedad viva.
  assert.doesNotMatch(source, /^const [A-Z0-9_]+ = Date\.now\(\);$/m);
  assert.doesNotMatch(source, /OBSERVED_AT_NOW_MS/);
  assert.equal(source.match(/Date\.now\(\)/g)?.length, 1);

  // La única muestra vive dentro del hook del reloj, no en el cuerpo del render de cada tarjeta.
  const hook = section(source, "function useWishlistClock", "function BandPriceFact");
  assert.equal(hook.match(/Date\.now\(\)/g)?.length, 1);
});

test("el reloj es uno solo, arranca en nulo tras montar y se limpia al desmontar", async () => {
  const source = await client();
  const hook = section(source, "function useWishlistClock", "function BandPriceFact");

  // Nulo hasta montar: el HTML del servidor y la primera hidratación dicen «sin reloj», y coinciden.
  assert.match(hook, /useState<number \| null>\(null\)/);
  // Primera muestra tras montar, dentro del efecto: nunca durante el render.
  assert.match(hook, /useEffect\(\(\) => \{[\s\S]*setNowMs\(\(current\) => advanceObservedAtClock\(current, Date\.now\(\)\)\)/);
  // Se refresca cada minuto, con el periodo del helper puro, para que una fecha futura pueda volverse pasada y
  // el borde de las 72 h se cruce con el reloj y no con una recarga.
  assert.match(hook, /setInterval\([\s\S]*OBSERVED_AT_CLOCK_TICK_MS/);
  // Y se limpia: sin esto cada montaje deja un temporizador vivo.
  assert.match(hook, /return \(\) => clearInterval\(timer\)/);

  // Un solo reloj para toda la lista: se crea en el padre y baja como prop a las tarjetas.
  assert.equal(source.match(/useWishlistClock\(\)/g)?.length, 2);
  const items = section(source, "function WishlistItems(", "export function WishlistClient(");
  assert.match(items, /const clockMs = useWishlistClock\(\);/);
  assert.match(items, /<WishlistCard[\s\S]*clockMs=\{clockMs\}/);
  // Ninguna tarjeta crea su propio reloj ni su propio temporizador: lo reciben por prop.
  assert.match(section(source, "interface WishlistCardProps", "function WishlistCard("), /readonly clockMs: number \| null;/);
  assert.doesNotMatch(section(source, "function WishlistCard(", "function WishlistItems("), /useWishlistClock|setInterval|useState/);
});

test("el distintivo aparece junto al precio principal visible, no solo dentro del detalle plegado", async () => {
  const source = await client();
  const card = section(source, "function WishlistCard(", "function WishlistItems(");

  // El precio grande de «Mejor precio» es lo que el usuario ve sin desplegar nada: su fecha va ahí mismo.
  assert.match(card, /<ObservedAtBadge amountMinor=\{winner\.priceMinor\} observedAt=\{winner\.observedAt\} nowMs=\{clockMs\} \/>/);

  const headline = card.indexOf("<ObservedAtBadge");
  const details = card.indexOf("<details");
  assert.ok(headline >= 0 && details > headline, "el distintivo del precio principal va antes del detalle plegado");
  assert.match(card.slice(0, details), /Mejor precio/);
  // Las dos bandas siguen teniendo su propio distintivo dentro del detalle: no se sustituye uno por otro.
  assert.match(card.slice(details), /<BandPriceFact label="Mín\. oficial"/);
  assert.match(card.slice(details), /<BandPriceFact label="Mín\. keys"/);
});
