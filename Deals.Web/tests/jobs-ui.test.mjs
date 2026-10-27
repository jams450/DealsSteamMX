import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

// La página de Jobs es un componente cliente: no hay runner de React en este repo, así que estas
// comprobaciones estructurales fijan el cableado que el contrato (`lib/contracts/jobs.test.ts`) sí prueba
// de comportamiento. Una opción `integer` se pinta como campo numérico etiquetado, con ayuda, rango y
// error accesibles; las casillas booleanas de los demás jobs siguen siendo casillas.
const client = new URL("../app/jobs/jobs-client.tsx", import.meta.url);

async function source() {
  return readFile(client, "utf8");
}

test("the integer option renders as a labelled numeric field with help, range and error", async () => {
  const text = await source();

  assert.match(text, /option\.type === "integer" \?/);
  assert.match(text, /type="number"/);
  assert.match(text, /inputMode="numeric"/);
  assert.match(text, /step=\{1\}/);
  assert.match(text, /min=\{option\.minValue \?\? undefined\}/);
  assert.match(text, /max=\{option\.maxValue \?\? undefined\}/);
  // La etiqueta y el error salen del mismo descriptor: no hay copy paralela con las horas hardcodeadas.
  assert.match(text, /label=\{option\.label\}/);
  assert.match(text, /error=\{error \?\? undefined\}/);
  assert.match(text, /aria-describedby=\{describedBy\}/);
  assert.match(text, /jobOptionRangeText\(option\)/);
  assert.match(text, /jobOptionValueError\(option, value\)/);
  assert.match(text, /option\.help \? \(/);
});

test("a fresh integer field starts from the declared default, never from a literal 72", async () => {
  const text = await source();

  assert.match(text, /option\.type === "integer" \? jobOptionInputText\(option, undefined\)/);
  assert.match(text, /value=\{jobOptionInputText\(option, optionValues\[option\.key\]\)\}/);
  assert.doesNotMatch(text, /\b72\b/);
});

test("boolean options keep the existing checkbox and the integer branch cannot render one", async () => {
  const text = await source();
  const checkboxes = text.match(/type="checkbox"/g) ?? [];

  // Dos casillas: la de cada opción booleana y la confirmación de un job destructivo.
  assert.equal(checkboxes.length, 2);
  assert.match(text, /typeof optionValues\[option\.key\] === "boolean"[\s\S]{0,160}option\.defaultValue === true/);
  assert.match(text, /: \(\s*<label key=\{option\.key\}[\s\S]{0,400}type="checkbox"/);
});

test("an invalid option blocks the run instead of silently sending the default", async () => {
  const text = await source();

  assert.match(text, /const invalidOption = selectedItem/);
  assert.match(text, /jobOptionValueError\(option, optionValues\[option\.key\]\) !== null/);
  assert.match(text, /invalidOption === null &&/);
  assert.match(text, /Corrige «\{invalidOption\.label\}» para poder ejecutar/);
  // El payload se construye (y puede lanzar) dentro del try, así que un rechazo se muestra, no se ignora.
  assert.match(text, /try \{\s*const payload = buildJobRunPayload\(/);
});

test("the Jobs UI does not invent the missing-price checkbox or a client-side userId", async () => {
  const text = await source();

  assert.doesNotMatch(text, /includeMissing/);
  assert.doesNotMatch(text, /userId/);
  assert.doesNotMatch(text, /ageHours/);
});
