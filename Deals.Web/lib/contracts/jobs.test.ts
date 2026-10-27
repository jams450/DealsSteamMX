// Contrato de opciones del panel de Jobs (spec S5, tarea T3). Un descriptor `integer` es un entero de
// horas que el usuario escribe; nunca se pinta como sí/no, nunca se redondea y nunca se sustituye en
// silencio por el default. Los descriptores booleanos de siempre (sin `type`) conservan su comportamiento.
// Se ejecuta con `node --test lib/contracts/jobs.test.ts` (sin instalar nada).
import test from "node:test";
import assert from "node:assert/strict";
import {
  buildJobRunPayload,
  formatJobOptionValues,
  jobOptionInputText,
  jobOptionRangeText,
  jobOptionValueError,
  normalizeJobCatalog,
  type JobOption
} from "./jobs.ts";

const AGE_HOURS: JobOption = {
  key: "ageHours",
  label: "Antigüedad mínima (horas)",
  help:
    "Incluye los precios viejos o inexistentes de tu propia wishlist. No cambia el monitor de frescura, que sigue fijo en 72 h.",
  type: "integer",
  defaultValue: 72,
  minValue: 1,
  maxValue: 720
};

const DRY_RUN: JobOption = {
  key: "dryRun",
  label: "Simulación",
  help: "",
  type: "boolean",
  defaultValue: false,
  minValue: null,
  maxValue: null
};

function catalogWith(options: readonly unknown[]) {
  return normalizeJobCatalog({
    items: [
      {
        job: "catalog.prices-refresh",
        displayName: "Precios pendientes de mi wishlist",
        description: "Refresca precios viejos o inexistentes de tu lista de deseados.",
        enabled: true,
        schedule: "solo manual (sin ejecución automática)",
        options
      }
    ]
  });
}

test("normalizeJobCatalog reads the integer descriptor with its bounds and default", () => {
  const catalog = catalogWith([
    {
      key: "ageHours",
      label: "Antigüedad mínima (horas)",
      help: AGE_HOURS.help,
      defaultValue: 72,
      type: "integer",
      minValue: 1,
      maxValue: 720
    }
  ]);

  assert.deepEqual(catalog.items[0].options[0], AGE_HOURS);
});

test("normalizeJobCatalog keeps boolean descriptors, including the legacy shape without type", () => {
  const catalog = catalogWith([
    { key: "dryRun", label: "Simulación", help: "No borra nada.", defaultValue: false },
    { key: "produce", label: "Generar", help: "", defaultValue: true, type: "boolean" }
  ]);

  assert.deepEqual(catalog.items[0].options, [
    { key: "dryRun", label: "Simulación", help: "No borra nada.", type: "boolean", defaultValue: false, minValue: null, maxValue: null },
    { key: "produce", label: "Generar", help: "", type: "boolean", defaultValue: true, minValue: null, maxValue: null }
  ]);
});

test("normalizeJobCatalog rejects a wrong-typed integer default instead of coercing it", () => {
  const [stringDefault, fractionalDefault, underDefault, overDefault] = [
    catalogWith([{ key: "ageHours", label: "Horas", help: "", defaultValue: "72", type: "integer", minValue: 1, maxValue: 720 }]),
    catalogWith([{ key: "ageHours", label: "Horas", help: "", defaultValue: 72.5, type: "integer", minValue: 1, maxValue: 720 }]),
    catalogWith([{ key: "ageHours", label: "Horas", help: "", defaultValue: 0, type: "integer", minValue: 1, maxValue: 720 }]),
    catalogWith([{ key: "ageHours", label: "Horas", help: "", defaultValue: 900, type: "integer", minValue: 1, maxValue: 720 }])
  ].map((catalog) => catalog.items[0].options[0]);

  for (const option of [stringDefault, fractionalDefault, underDefault, overDefault]) {
    assert.equal(option.type, "integer");
    // Nulo, no 72: la UI deja el campo vacío y el usuario escribe las horas que quiere.
    assert.equal(option.defaultValue, null);
  }

  // Sin default válido y sin valor del usuario no se envía nada: el builder rechaza en vez de mandar 72.
  assert.throws(() => buildJobRunPayload([stringDefault], {}, false), /Horas/);
});

test("normalizeJobCatalog drops non-integer bounds instead of reading them as numbers", () => {
  const catalog = catalogWith([
    { key: "ageHours", label: "Horas", help: "", defaultValue: 72, type: "integer", minValue: "1", maxValue: 720.5 }
  ]);
  const option = catalog.items[0].options[0];

  assert.equal(option.defaultValue, 72);
  assert.equal(option.minValue, null);
  assert.equal(option.maxValue, null);
});

test("buildJobRunPayload sends the hours the user wrote as a number", () => {
  assert.deepEqual(buildJobRunPayload([AGE_HOURS], { ageHours: "168" }, false), {
    options: { ageHours: 168 },
    confirm: false
  });
  assert.deepEqual(buildJobRunPayload([AGE_HOURS], { ageHours: 168 }, false), {
    options: { ageHours: 168 },
    confirm: false
  });
  assert.deepEqual(buildJobRunPayload([AGE_HOURS], { ageHours: "1" }, true), { options: { ageHours: 1 }, confirm: true });
  assert.deepEqual(buildJobRunPayload([AGE_HOURS], { ageHours: "720" }, false), {
    options: { ageHours: 720 },
    confirm: false
  });
});

test("buildJobRunPayload refuses an empty, fractional or out-of-range integer instead of sending 72", () => {
  const invalid = [{ ageHours: undefined }, { ageHours: "" }, { ageHours: "   " }, { ageHours: "72.5" }, { ageHours: 72.5 }, { ageHours: "1e2" }, { ageHours: "abc" }, { ageHours: true }, { ageHours: 0 }, { ageHours: 721 }, { ageHours: "-24" }];

  for (const values of invalid) {
    assert.throws(
      () => buildJobRunPayload([AGE_HOURS], values, false),
      (error: unknown) => {
        assert.ok(error instanceof Error);
        assert.match(error.message, /Antigüedad mínima \(horas\)/);
        return true;
      },
      `aceptó ${JSON.stringify(values)}`
    );
  }
});

test("jobOptionValueError explains why an integer value is unusable", () => {
  assert.equal(jobOptionValueError(AGE_HOURS, "72"), null);
  assert.equal(jobOptionValueError(AGE_HOURS, 720), null);
  assert.match(jobOptionValueError(AGE_HOURS, "") ?? "", /entero/);
  assert.match(jobOptionValueError(AGE_HOURS, "72.5") ?? "", /entero/);
  assert.match(jobOptionValueError(AGE_HOURS, "abc") ?? "", /entero/);
  assert.match(jobOptionValueError(AGE_HOURS, 0) ?? "", /1 y 720/);
  assert.match(jobOptionValueError(AGE_HOURS, 721) ?? "", /1 y 720/);
  assert.equal(jobOptionValueError(DRY_RUN, undefined), null);
});

test("buildJobRunPayload never forwards userId or an option the catalog did not declare", () => {
  const payload = buildJobRunPayload(
    [AGE_HOURS, DRY_RUN],
    { ageHours: "24", dryRun: true, userId: 7, includeMissing: false, confirm: true },
    false
  );

  assert.deepEqual(payload, { options: { ageHours: 24, dryRun: true }, confirm: false });
  assert.deepEqual(Object.keys(payload.options), ["ageHours", "dryRun"]);
});

test("buildJobRunPayload keeps the legacy boolean behavior for a job the change does not touch", () => {
  const options = [
    { key: "produce", label: "Generar", help: "", defaultValue: true },
    { key: "dispatch", label: "Enviar", help: "", defaultValue: true }
  ] as unknown as readonly JobOption[];

  assert.deepEqual(buildJobRunPayload(options, {}, false), {
    options: { produce: true, dispatch: true },
    confirm: false
  });
  assert.deepEqual(buildJobRunPayload(options, { dispatch: false }, true), {
    options: { produce: true, dispatch: false },
    confirm: true
  });
  assert.deepEqual(buildJobRunPayload(options, { unknown: false }, false), {
    options: { produce: true, dispatch: true },
    confirm: false
  });
});

test("formatJobOptionValues shows hours as a number and keeps sí/no for booleans", () => {
  assert.deepEqual(formatJobOptionValues([AGE_HOURS], {}), ["Antigüedad mínima (horas): 72"]);
  assert.deepEqual(formatJobOptionValues([AGE_HOURS], { ageHours: "168" }), ["Antigüedad mínima (horas): 168"]);
  assert.deepEqual(formatJobOptionValues([DRY_RUN], {}), ["Simulación: no"]);
  assert.deepEqual(formatJobOptionValues([DRY_RUN], { dryRun: true }), ["Simulación: sí"]);
  assert.deepEqual(formatJobOptionValues([AGE_HOURS], { ageHours: "72.5" }), ["Antigüedad mínima (horas): —"]);
  assert.deepEqual(formatJobOptionValues([AGE_HOURS], { ageHours: "" }), ["Antigüedad mínima (horas): —"]);
});

test("jobOptionInputText keeps the user's text verbatim and falls back only to a valid default", () => {
  assert.equal(jobOptionInputText(AGE_HOURS, undefined), "72");
  assert.equal(jobOptionInputText(AGE_HOURS, "72."), "72.");
  assert.equal(jobOptionInputText(AGE_HOURS, ""), "");
  assert.equal(jobOptionInputText(AGE_HOURS, 24), "24");
  assert.equal(jobOptionInputText({ ...AGE_HOURS, defaultValue: null }, undefined), "");
});

test("jobOptionRangeText states the declared range for the accessible description", () => {
  assert.equal(jobOptionRangeText(AGE_HOURS), "Rango permitido: 1–720.");
  assert.equal(jobOptionRangeText({ ...AGE_HOURS, maxValue: null }), "Mínimo permitido: 1.");
  assert.equal(jobOptionRangeText({ ...AGE_HOURS, minValue: null }), "Máximo permitido: 720.");
  assert.equal(jobOptionRangeText({ ...AGE_HOURS, minValue: null, maxValue: null }), null);
  assert.equal(jobOptionRangeText(DRY_RUN), null);
});
