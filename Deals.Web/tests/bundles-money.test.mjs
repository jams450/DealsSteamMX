import test from "node:test";
import assert from "node:assert/strict";
import { normalizeBundleCatalog } from "../lib/contracts/bundles.ts";
import {
  allocatedPerGameMoneyView,
  fxDisclosureText,
  tierMoneyView
} from "../app/bundles/_lib/bundle-money.ts";

// S1: el catálogo de bundles conserva su diseño y suma USD/MXN. El servidor ya convirtió con la tasa
// persistida; este módulo solo decide qué par se pinta, cuál es estimación (≈) y qué metadatos FX la
// respaldan. Nada de aritmética de tipos de cambio en el navegador y ningún cero inventado.

function money(overrides = {}) {
  return {
    usdMinor: 1299,
    mxnMinor: 23942,
    fxRate: 18.4321,
    fxRateDate: "2026-10-19",
    fxSource: "banxico",
    pricingType: "regional",
    ...overrides
  };
}

function tierFrom(overrides = {}) {
  const catalog = normalizeBundleCatalog({
    items: [
      {
        source: "itad",
        bundleKey: "bundle-1",
        title: "Mega Bundle",
        tiers: [
          {
            priceMinor: 1299,
            currency: "USD",
            addon: false,
            itemsComplete: true,
            itemCount: 3,
            eligibleGameCount: 2,
            allocatedPerGameMinor: 650,
            allocationStatus: "ok",
            priceMoney: money(),
            allocatedPerGameMoney: money({ usdMinor: 650, mxnMinor: 11971 }),
            ...overrides
          }
        ]
      }
    ]
  });
  const tier = catalog.items[0].tiers[0];
  assert.ok(tier, "el tier de prueba debe normalizar; si no, la prueba no mide nada");
  return tier;
}

test("precio nativo en USD: el MXN convertido es estimación y se pinta con ≈, sin recalcular", () => {
  const view = tierMoneyView(tierFrom());
  assert.ok(view);
  assert.equal(view.nativeCurrency, "USD");
  assert.deepEqual(view.displayLegs.map((leg) => leg.currency), ["MXN"]);
  const mxn = view.displayLegs[0];
  assert.equal(mxn.amountMinor, 23942, "la cifra es la del servidor, no la del navegador");
  assert.equal(mxn.approximate, true);
  assert.equal(mxn.isNative, false);
  assert.equal(view.hasApproximation, true);
  assert.deepEqual(view.fx, { rate: 18.4321, rateDate: "2026-10-19", source: "banxico" });
  // Ambas monedas quedan visibles: USD en la línea nativa y MXN en la fila compacta, sin duplicar USD.
  assert.deepEqual(
    [view.nativeCurrency, ...view.displayLegs.map((leg) => leg.currency)].sort(),
    ["MXN", "USD"]
  );
  assert.equal(view.displayLegs.some((leg) => leg.isNative), false);
});

test("precio nativo en MXN: el USD convertido es estimación y el MXN no se repite", () => {
  const view = tierMoneyView(
    tierFrom({ priceMinor: 23942, currency: "MXN", priceMoney: money({ usdMinor: 1299, mxnMinor: 23942 }) })
  );
  assert.ok(view);
  assert.equal(view.nativeCurrency, "MXN");
  assert.deepEqual(view.displayLegs.map((leg) => leg.currency), ["USD"]);
  assert.equal(view.displayLegs[0].amountMinor, 1299);
  assert.equal(view.displayLegs[0].approximate, true);
  assert.equal(view.hasApproximation, true);
});

// Defensivo: el servidor actual no reclama USD/MXN para una tercera moneda (emite `unconverted` sin
// cifras), así que este payload no viene de él. Si algún emisor futuro lo manda, ninguna de las dos cifras
// es la publicada y ambas se marcan como estimación en vez de presentar una como precio de ITAD.
test("una moneda nativa que no es USD ni MXN marca las dos cifras como estimación", () => {
  const view = tierMoneyView(
    tierFrom({ priceMinor: 1099, currency: "EUR", priceMoney: money({ usdMinor: 1188, mxnMinor: 21900 }) })
  );
  assert.ok(view);
  assert.equal(view.nativeCurrency, "EUR");
  assert.deepEqual(view.displayLegs.map((leg) => leg.currency), ["USD", "MXN"]);
  assert.deepEqual(view.displayLegs.map((leg) => leg.amountMinor), [1188, 21900]);
  assert.deepEqual(view.displayLegs.map((leg) => leg.approximate), [true, true]);
});

test("sin tasa persistida no hay cifra convertida: no disponible, nunca cero", () => {
  const view = tierMoneyView(
    tierFrom({
      priceMoney: money({ mxnMinor: null, fxRate: null, fxRateDate: null, fxSource: null, pricingType: "unconverted" })
    })
  );
  assert.ok(view);
  const mxn = view.displayLegs.find((leg) => leg.currency === "MXN");
  assert.ok(mxn);
  assert.equal(mxn.amountMinor, null);
  assert.equal(mxn.approximate, true);
  assert.equal(view.hasApproximation, false, "sin cifra convertida no se anuncia una conversión");
  assert.equal(view.fx, null, "sin tasa no hay metadatos que mostrar");
});

test("sin bloque de dinero el tier no inventa filas", () => {
  assert.equal(tierMoneyView(tierFrom({ priceMoney: undefined })), null);
  assert.equal(tierMoneyView(tierFrom({ priceMoney: null })), null);
});

test("un tier sin precio nativo no genera filas duales aunque traiga bloque de dinero", () => {
  const catalog = normalizeBundleCatalog({
    items: [
      {
        source: "itad",
        bundleKey: "bundle-1",
        title: "Mega Bundle",
        tiers: [
          {
            priceMinor: null,
            currency: null,
            addon: false,
            itemsComplete: true,
            itemCount: 3,
            eligibleGameCount: 2,
            allocatedPerGameMinor: null,
            allocationStatus: "no_tier_price",
            priceMoney: money(),
            allocatedPerGameMoney: money()
          }
        ]
      }
    ]
  });
  assert.equal(tierMoneyView(catalog.items[0].tiers[0]), null);
});

test("un bloque de dinero sin ninguna cifra no produce filas", () => {
  const view = tierMoneyView(
    tierFrom({ priceMoney: money({ usdMinor: null, mxnMinor: null, fxRate: null, fxRateDate: null, fxSource: null }) })
  );
  assert.equal(view, null);
});

test("el reparto por juego usa el bloque propio y exige estado asignable", () => {
  const ok = allocatedPerGameMoneyView(tierFrom());
  assert.ok(ok);
  assert.deepEqual(ok.displayLegs.map((leg) => leg.currency), ["MXN"]);
  assert.equal(ok.displayLegs[0].amountMinor, 11971);
  assert.equal(ok.displayLegs[0].approximate, true);
  assert.equal(ok.hasApproximation, true);

  const noTierPrice = allocatedPerGameMoneyView(
    tierFrom({ allocationStatus: "no_tier_price", allocatedPerGameMinor: null, allocatedPerGameMoney: null })
  );
  assert.equal(noTierPrice, null, "un tier sin reparto no muestra reparto convertido");
});

test("el reparto convertido no reutiliza el bloque del precio del tier", () => {
  const view = allocatedPerGameMoneyView(tierFrom({ allocatedPerGameMoney: null }));
  assert.equal(view, null, "sin bloque propio no se deriva del precio total");
});

test("la nota de FX nombra tasa, fecha y fuente, y omite lo que falta", () => {
  assert.equal(
    fxDisclosureText({ rate: 18.4321, rateDate: "2026-10-19", source: "banxico" }, "USD"),
    "Tasa 18.4321 MXN por USD · 19/10/2026 · banxico"
  );
  assert.equal(fxDisclosureText({ rate: 18.4, rateDate: null, source: null }, "USD"), "Tasa 18.40 MXN por USD");
  assert.equal(fxDisclosureText({ rate: 19, rateDate: null, source: null }, "USD"), "Tasa 19.00 MXN por USD");
  assert.equal(fxDisclosureText({ rate: null, rateDate: "2026-10-19", source: null }, "USD"), "19/10/2026");
  assert.equal(fxDisclosureText({ rate: null, rateDate: null, source: "banxico" }, "USD"), "banxico");
  assert.equal(fxDisclosureText({ rate: null, rateDate: null, source: null }, "USD"), null);
  assert.equal(fxDisclosureText(null, "USD"), null);
});

// La tasa NO es siempre "MXN por USD": es la fila persistida que el servidor usó, y su dirección la fija
// la moneda nativa del tier. Un tier en MXN usa una fila MXN->USD explícita (p. ej. 0.055 USD por MXN) y
// el navegador no tiene permitido invertirla.
test("la nota de FX respeta la dirección de la tasa según la moneda nativa", () => {
  const usdNative = fxDisclosureText({ rate: 18.2, rateDate: "2026-10-19", source: "banxico" }, "USD");
  assert.equal(usdNative, "Tasa 18.20 MXN por USD · 19/10/2026 · banxico");

  const mxnNative = fxDisclosureText({ rate: 0.055, rateDate: "2026-10-19", source: "banxico" }, "MXN");
  assert.equal(mxnNative, "Tasa 0.055 USD por MXN · 19/10/2026 · banxico");
  assert.doesNotMatch(mxnNative, /MXN por USD/, "una fila MXN->USD no se presenta como MXN por USD");

  // Tercera moneda: la dirección de la fila es desconocida. Se omite la cifra, no se adivina.
  const unknown = fxDisclosureText({ rate: 18.2, rateDate: "2026-10-19", source: "banxico" }, "EUR");
  assert.equal(unknown, "19/10/2026 · banxico");
  assert.doesNotMatch(unknown, /Tasa/, "sin dirección conocida no se pinta una tasa que se lea al revés");
  assert.doesNotMatch(unknown, /18\.2/);

  const noNativeCurrency = fxDisclosureText({ rate: 18.2, rateDate: "2026-10-19", source: null }, null);
  assert.equal(noNativeCurrency, "19/10/2026");
});

test("el view de un tier en MXN expone la moneda nativa para etiquetar la tasa", () => {
  const view = tierMoneyView(
    tierFrom({
      priceMinor: 23942,
      currency: "MXN",
      priceMoney: money({ usdMinor: 1299, mxnMinor: 23942, fxRate: 0.055, pricingType: "fx_estimate" })
    })
  );
  assert.ok(view);
  assert.equal(view.nativeCurrency, "MXN");
  assert.equal(
    fxDisclosureText(view.fx, view.nativeCurrency),
    "Tasa 0.055 USD por MXN · 19/10/2026 · banxico"
  );
});

test("el tier conserva el precio nativo y su moneda originales", () => {
  const tier = tierFrom();
  assert.equal(tier.priceMinor, 1299);
  assert.equal(tier.currency, "USD");
  const view = tierMoneyView(tier);
  assert.ok(view);
  // El precio nativo no se sustituye por la conversión: la fila nativa del diseño sigue disponible.
  assert.equal(tier.allocatedPerGameMinor, 650);
  assert.equal(view.nativeCurrency, "USD");
});

test("un tier sin moneda nativa no pinta cifras que la línea nativa da por ausentes", () => {
  // La línea nativa dice «Sin precio en ITAD» cuando falta la moneda: filas USD/MXN al lado se leerían
  // como un precio que la propia tarjeta acaba de negar.
  const catalog = normalizeBundleCatalog({
    items: [
      {
        source: "itad",
        bundleKey: "bundle-1",
        title: "Mega Bundle",
        tiers: [
          {
            priceMinor: 1299,
            currency: null,
            addon: false,
            itemsComplete: true,
            itemCount: 3,
            eligibleGameCount: 2,
            allocatedPerGameMinor: 650,
            allocationStatus: "ok",
            priceMoney: money(),
            allocatedPerGameMoney: money()
          }
        ]
      }
    ]
  });
  const tier = catalog.items[0].tiers[0];
  assert.equal(tier.currency, null);
  assert.equal(tierMoneyView(tier), null);
  assert.equal(allocatedPerGameMoneyView(tier), null);
});
