#!/usr/bin/env node
// Exporta la biblioteca Epic/GOG que Heroic Games Launcher ya tiene en disco a un JSON que DealExt
// puede subir a mano (POST /api/library/provider-export). Sin red, sin credenciales y sin dependencias:
// solo built-ins de Node >= 22.
//
// Contrato (L4 de odd/tasks/heroic-library-export.md), campo por campo y en este orden:
//   { format: "dealext-library", version: 1, source: "heroic", exportedAt: ISO-UTC,
//     games: [{ store: "epic"|"gog", storeGameId, title, isInstalled }] }
// El estado de posesión lo fuerza el servidor; aquí solo viaja `isInstalled`.
//
// Reglas de la casa, todas medidas contra el código de Heroic:
//  1. Solo se abren dos archivos, siempre por nombre fijo dentro de `store_cache/`:
//     `legendary_library.json` (clave `.library`) y `gog_library.json` (clave `.games`). Los archivos
//     de sesión (`legendary/user.json`, tokens de GOG) jamás se tocan: no hacen falta ni se citan.
//  2. Se escribe una lista blanca de campos. El `GameInfo` de Heroic trae `install.install_path`,
//     `folder_name`, `save_path` y `art_*`; nada de eso sale de este proceso.
//  3. Caché malformado, fila inválida o total vacío = fallo cerrado. Nunca se rellena un valor por
//     defecto ni se recorta en silencio: un export incompleto con aspecto de completo es peor que uno
//     que no existe.
//  4. Los cachés no tienen TTL (Heroic los crea con `max_value_lifespan = null`, ver
//     `src/backend/storeManagers/{gog,legendary}/electronStores.ts`), así que se avisa de su edad y se
//     recuerda que esto no es una consulta en vivo.
//
// Uso y procedimiento operativo: docs/HEROIC_EXPORT.md.

import { realpath, readFile, stat, writeFile } from "node:fs/promises";
import { realpathSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const FORMAT = "dealext-library";
export const VERSION = 1;
export const SOURCE = "heroic";

// Mismo tope que el import existente de la app (`LIBRARY_IMPORT_MAX_BYTES` en
// Deals.Web/app/library/_lib/library-contract.ts y el chequeo del BFF): 10 MiB.
export const MAX_OUTPUT_BYTES = 10 * 1024 * 1024;
// Mismo tope de filas que el import: 10 000.
export const MAX_GAMES = 10_000;

export const DEFAULT_OUTPUT = "dealext-heroic-library.json";

// Sin TTL en el caché no hay forma de saber si Heroic sigue abierto: a partir de una semana se avisa.
export const STALE_CACHE_WARNING_DAYS = 7;

const DAY_MS = 24 * 60 * 60 * 1000;
// Mismos topes por campo que el import de la app (`HeroicLibraryExportLimits` en
// Deals.BusinessLogic/Models/Library/HeroicLibraryExport.cs: `MaxStoreGameIdLength = 64`,
// `MaxTitleLength = 256`). Escribir más largo produciría un archivo que el servidor rechaza entero.
const MAX_ID_LENGTH = 64;
const MAX_TITLE_LENGTH = 256;
const CONTROL_CHARS = /[\u0000-\u001f\u007f]/;

// Solo estas dos tiendas. El `runner` y el nombre del archivo salen de
// `src/backend/storeManagers/<tienda>/electronStores.ts` (verificado en v2.15.2 y v2.22.4).
const STORES = {
  epic: {
    label: "Epic",
    runner: "legendary",
    cacheFile: "legendary_library.json",
    payloadKey: "library",
    timestampKey: "__timestamp.library",
  },
  gog: {
    label: "GOG",
    runner: "gog",
    cacheFile: "gog_library.json",
    payloadKey: "games",
    timestampKey: "__timestamp.games",
  },
};

export const SUPPORTED_STORES = Object.keys(STORES);

const HELP = `Uso: node scripts/export-heroic-library.mjs --dir <carpeta de Heroic> [opciones]

  --dir <ruta>      Carpeta de configuración de Heroic (la que contiene store_cache/). Obligatoria.
  --stores <lista>  Tiendas separadas por coma: epic,gog. Por defecto se exportan las dos.
  --output <ruta>   Archivo JSON de salida. Por defecto ${DEFAULT_OUTPUT} en el directorio actual.
  --force           Sobrescribe --output si ya existe.
  -h, --help        Muestra esta ayuda.

Lee únicamente store_cache/legendary_library.json (Epic) y store_cache/gog_library.json (GOG).
No abre archivos de sesión, no usa la red y no sube nada: el JSON se sube a mano en DealExt.
Los avisos y los conteos van a stderr; stdout queda vacío y el JSON se escribe solo en --output.
Códigos de salida: 0 correcto, 1 el caché o la salida no son utilizables, 2 argumentos inválidos.

Antes de exportar, abre Heroic y refresca la biblioteca de cada tienda: estos archivos solo se
reescriben cuando Heroic refresca.
`;

export class ExportError extends Error {
  constructor(message, exitCode = 1) {
    super(message);
    this.name = "ExportError";
    this.exitCode = exitCode;
  }
}

function usageError(message) {
  return new ExportError(message, 2);
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

async function realpathOrSelf(target) {
  try {
    return await realpath(target);
  } catch {
    return path.resolve(target);
  }
}

function samePath(a, b) {
  const normalize = (value) => (process.platform === "win32" ? value.toLowerCase() : value);
  return normalize(path.resolve(a)) === normalize(path.resolve(b));
}

function isInside(parent, candidate) {
  const relative = path.relative(parent, candidate);
  return relative !== "" && !relative.startsWith("..") && !path.isAbsolute(relative);
}

function plural(count, singular, pluralForm) {
  return `${count} ${count === 1 ? singular : pluralForm}`;
}

function parseStores(raw) {
  const entries = raw.split(",").map((entry) => entry.trim().toLowerCase());
  if (entries.some((entry) => entry === "")) {
    throw usageError(`--stores recibió una lista vacía o con huecos: ${JSON.stringify(raw)}. Usa epic,gog.`);
  }
  const stores = [];
  for (const entry of entries) {
    if (!SUPPORTED_STORES.includes(entry)) {
      throw usageError(`tienda no soportada: ${entry}. Solo se exportan ${SUPPORTED_STORES.join(" y ")}.`);
    }
    if (!stores.includes(entry)) stores.push(entry);
  }
  return stores;
}

export function parseArgs(argv) {
  const options = {
    dir: null,
    stores: [...SUPPORTED_STORES],
    output: DEFAULT_OUTPUT,
    force: false,
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    const equalsAt = arg.startsWith("--") ? arg.indexOf("=") : -1;
    const name = equalsAt === -1 ? arg : arg.slice(0, equalsAt);
    const inlineValue = equalsAt === -1 ? null : arg.slice(equalsAt + 1);

    const takeValue = () => {
      if (inlineValue !== null) {
        if (inlineValue === "") throw usageError(`${name} necesita un valor`);
        return inlineValue;
      }
      const next = argv[index + 1];
      if (next === undefined) throw usageError(`${name} necesita un valor`);
      index += 1;
      return next;
    };

    switch (name) {
      case "-h":
      case "--help":
        options.help = true;
        break;
      case "--force":
        if (inlineValue !== null) throw usageError("--force no lleva valor");
        options.force = true;
        break;
      case "--dir":
        options.dir = takeValue();
        break;
      case "--output":
        options.output = takeValue();
        break;
      case "--stores":
        options.stores = parseStores(takeValue());
        break;
      default:
        throw usageError(`argumento desconocido: ${arg}`);
    }
  }

  if (options.help) return options;
  if (!options.dir) {
    throw usageError("falta --dir con la carpeta de configuración de Heroic (la que contiene store_cache/).");
  }
  return options;
}

// `__timestamp.<clave>` es la marca que Heroic escribe junto a la lista (`CacheStore.set`, en
// `src/backend/cache.ts`). Si falta o no se puede leer, la fecha del archivo es la siguiente mejor
// evidencia: se informa de cuál se usó para no fingir precisión.
function resolveRefreshedAt(timestampValue, stats) {
  if (typeof timestampValue === "string") {
    const parsed = new Date(timestampValue);
    if (!Number.isNaN(parsed.getTime())) return { at: parsed, from: "timestamp" };
  }
  return { at: stats.mtime, from: "mtime" };
}

async function readStoreCache(store, configDir) {
  const definition = STORES[store];
  const cachePath = path.join(configDir, "store_cache", definition.cacheFile);

  let raw;
  try {
    raw = await readFile(cachePath, "utf8");
  } catch (error) {
    if (error.code === "ENOENT") {
      const alternative = SUPPORTED_STORES.filter((candidate) => candidate !== store).join(",");
      throw new ExportError(
        `falta el caché de ${store}: ${cachePath}\n` +
          `Abre Heroic, refresca la biblioteca de ${definition.label} y vuelve a exportar. ` +
          `Si no usas ${definition.label}, omítela con --stores ${alternative}.`,
      );
    }
    throw new ExportError(`no se pudo leer el caché de ${store} (${cachePath}): ${error.message}`);
  }

  let payload;
  try {
    payload = JSON.parse(raw);
  } catch (error) {
    throw new ExportError(
      `el caché de ${store} no es JSON válido (${cachePath}): ${error.message}\n` +
        "Abre Heroic y refresca esa biblioteca: Heroic reescribe el archivo completo.",
    );
  }

  if (!isPlainObject(payload)) {
    throw new ExportError(`el caché de ${store} no tiene la forma esperada (${cachePath}): se esperaba un objeto con "${definition.payloadKey}".`);
  }

  const games = payload[definition.payloadKey];
  if (!Array.isArray(games)) {
    throw new ExportError(
      `el caché de ${store} no trae la lista "${definition.payloadKey}" (${cachePath}).\n` +
        "Abre Heroic y refresca esa biblioteca; si el archivo viene de otra versión, reexpórtalo desde Heroic.",
    );
  }

  const stats = await stat(cachePath);
  const { at, from } = resolveRefreshedAt(payload[definition.timestampKey], stats);

  return { store, label: definition.label, cachePath, games, refreshedAt: at, refreshedFrom: from };
}

function validateRow(store, raw, index, cachePath) {
  const definition = STORES[store];
  const where = `${definition.cacheFile}, fila ${index + 1}`;

  if (!isPlainObject(raw)) {
    throw new ExportError(`${where}: la fila no es un objeto. Caché: ${cachePath}.`);
  }

  // El `runner` es la prueba barata de que el archivo es el de esta tienda y no el de la otra.
  if (raw.runner !== definition.runner) {
    throw new ExportError(
      `${where}: el campo runner debe ser "${definition.runner}" y llegó ${JSON.stringify(raw.runner)}. ` +
        `¿El archivo pertenece a otra tienda? Caché: ${cachePath}.`,
    );
  }

  const storeGameId = raw.app_name;
  if (typeof storeGameId !== "string" || storeGameId.trim() === "") {
    throw new ExportError(`${where}: falta app_name (el id de la tienda). Caché: ${cachePath}.`);
  }
  if (storeGameId !== storeGameId.trim()) {
    throw new ExportError(`${where}: app_name trae espacios al inicio o al final (${JSON.stringify(storeGameId)}); no se corrige en silencio. Caché: ${cachePath}.`);
  }
  if (storeGameId.length > MAX_ID_LENGTH) {
    throw new ExportError(`${where}: app_name supera ${MAX_ID_LENGTH} caracteres (${storeGameId.length}). Caché: ${cachePath}.`);
  }
  if (CONTROL_CHARS.test(storeGameId)) {
    throw new ExportError(`${where}: app_name contiene caracteres de control. Caché: ${cachePath}.`);
  }
  // El id de GOG es el id numérico del producto; un slug aquí sería una fila de otra fuente. El
  // número de dígitos no se limita aquí: la cota que manda es la de longitud del import (64), igual
  // que para Epic. Un tope menor rechazaría ids que la app sí acepta.
  if (store === "gog" && !/^[0-9]+$/.test(storeGameId)) {
    throw new ExportError(`${where}: el id de GOG debe ser numérico y llegó ${JSON.stringify(storeGameId)}. Caché: ${cachePath}.`);
  }

  const title = typeof raw.title === "string" ? raw.title.trim() : "";
  if (title === "") {
    throw new ExportError(`${where}: el título está vacío o ausente. Caché: ${cachePath}.`);
  }
  if (title.length > MAX_TITLE_LENGTH) {
    throw new ExportError(`${where}: el título supera ${MAX_TITLE_LENGTH} caracteres (${title.length}). Caché: ${cachePath}.`);
  }
  if (CONTROL_CHARS.test(title)) {
    throw new ExportError(`${where}: el título contiene caracteres de control. Caché: ${cachePath}.`);
  }

  // Booleano estricto: el import exige `installed` booleano y un "false" por defecto mentiría.
  if (typeof raw.is_installed !== "boolean") {
    throw new ExportError(
      `${where}: is_installed debe ser booleano (true/false) y llegó ${JSON.stringify(raw.is_installed)}. Caché: ${cachePath}.`,
    );
  }

  return { store, storeGameId, title, isInstalled: raw.is_installed };
}

function collectGames(cache) {
  const seen = new Set();
  const rows = [];
  let duplicates = 0;

  cache.games.forEach((raw, index) => {
    // Heroic inserts this internal runtime component before the owned games, even on a fresh cache.
    // Source: Heroic src/backend/storeManagers/gog/library.ts, refresh(): redistGameInfo.
    // Do not relax numeric product IDs or skip other malformed entries.
    if (cache.store === "gog" && raw?.runner === "gog" && raw?.app_name === "gog-redist") {
      console.error("[heroic-export] gog: omitido gog-redist (componente interno, no es un juego)");
      return;
    }
    const row = validateRow(cache.store, raw, index, cache.cachePath);
    const key = `${row.store}\u0000${row.storeGameId}`;
    if (seen.has(key)) {
      duplicates += 1;
      return;
    }
    seen.add(key);
    rows.push(row);
  });

  return { rows, duplicates };
}

// Orden determinista y documentado: tienda ascendente (epic < gog) y, dentro de ella, id ascendente por
// unidad de código. Nada de `localeCompare`: el resultado no debe depender del locale de la máquina.
function compareRows(a, b) {
  if (a.store !== b.store) return a.store < b.store ? -1 : 1;
  if (a.storeGameId !== b.storeGameId) return a.storeGameId < b.storeGameId ? -1 : 1;
  if (a.title !== b.title) return a.title < b.title ? -1 : 1;
  return 0;
}

export function buildEnvelope(rows, exportedAt = new Date()) {
  return {
    format: FORMAT,
    version: VERSION,
    source: SOURCE,
    exportedAt: exportedAt.toISOString(),
    // Se reconstruye cada fila: si algo más se colara en el objeto, no llegaría al archivo.
    games: rows.map((row) => ({
      store: row.store,
      storeGameId: row.storeGameId,
      title: row.title,
      isInstalled: row.isInstalled,
    })),
  };
}

export function serializeEnvelope(envelope, maxBytes = MAX_OUTPUT_BYTES) {
  const body = `${JSON.stringify(envelope, null, 2)}\n`;
  const bytes = Buffer.byteLength(body, "utf8");
  if (bytes > maxBytes) {
    throw new ExportError(
      `el JSON de salida supera el tope permitido (${bytes} bytes; máximo ${maxBytes} bytes, 10 MiB en el límite real). ` +
        "Reduce la selección con --stores o revisa que el caché no esté duplicado.",
    );
  }
  return body;
}

async function resolveOutputTarget(options) {
  const realConfigDir = await realpathOrSelf(options.dir);
  const cacheDir = path.join(realConfigDir, "store_cache");
  const absoluteOutput = path.resolve(options.output);
  const realOutputParent = await realpathOrSelf(path.dirname(absoluteOutput));
  // Se resuelve el archivo mismo, no solo su carpeta: un `--output` que sea un symlink a un caché
  // escribiría el JSON dentro del caché aunque la ruta escrita parezca distinta.
  const writtenOutput = path.join(realOutputParent, path.basename(absoluteOutput));
  const realOutput = await realpathOrSelf(writtenOutput);

  for (const store of options.stores) {
    const cachePath = await realpathOrSelf(path.join(cacheDir, STORES[store].cacheFile));
    if (samePath(realOutput, cachePath)) {
      throw new ExportError(`la salida no puede ser un caché de entrada (${absoluteOutput}). Elige otra ruta con --output.`);
    }
  }

  // Nada se escribe dentro de la configuración de Heroic, con o sin `--force`: ahí no solo están
  // `store_cache/`, también las credenciales de las tiendas (`gog_store/auth.json`, `legendary/user.json`).
  // Un export no debe poder convertir un archivo de sesión en su salida. Se compara el realpath, así que
  // ni un symlink a un archivo de la configuración ni un directorio enlazado desde fuera se cuelan.
  if (samePath(realOutput, realConfigDir) || isInside(realConfigDir, realOutput)) {
    throw new ExportError(
      `la salida no puede escribirse dentro de la carpeta de configuración de Heroic (${absoluteOutput}): ` +
        "ahí viven los cachés de Heroic (store_cache/) y las credenciales de las tiendas (gog_store/, legendary/). " +
        "Elige una ruta fuera de esa carpeta con --output.",
    );
  }

  let existing = null;
  try {
    existing = await stat(realOutput);
  } catch (error) {
    if (error.code !== "ENOENT") throw new ExportError(`no se pudo comprobar la salida ${absoluteOutput}: ${error.message}`);
  }
  if (existing?.isDirectory()) {
    throw new ExportError(`la salida es un directorio (${absoluteOutput}). Indica un archivo con --output.`);
  }
  if (existing && !options.force) {
    throw new ExportError(`la salida ya existe: ${absoluteOutput}. Usa --force para sobrescribirla o elige otra ruta con --output.`);
  }

  return { realOutput };
}

export async function runExport(options) {
  const configDir = path.resolve(options.dir);
  let dirStats;
  try {
    dirStats = await stat(configDir);
  } catch (error) {
    if (error.code === "ENOENT") {
      throw new ExportError(
        `no existe la carpeta de configuración de Heroic: ${configDir}\n` +
          "Pasa --dir con la carpeta que contiene store_cache/ (ver docs/HEROIC_EXPORT.md, §2).",
      );
    }
    throw new ExportError(`no se pudo leer ${configDir}: ${error.message}`);
  }
  if (!dirStats.isDirectory()) {
    throw new ExportError(`${configDir} no es una carpeta. --dir debe apuntar a la configuración de Heroic.`);
  }

  const caches = [];
  for (const store of options.stores) {
    caches.push(await readStoreCache(store, configDir));
  }

  const rows = [];
  let duplicates = 0;
  const storeStats = [];
  for (const cache of caches) {
    const collected = collectGames(cache);
    duplicates += collected.duplicates;
    rows.push(...collected.rows);
    storeStats.push({
      store: cache.store,
      count: collected.rows.length,
      installed: collected.rows.filter((row) => row.isInstalled).length,
      cachePath: cache.cachePath,
      refreshedAt: cache.refreshedAt,
      refreshedFrom: cache.refreshedFrom,
    });
  }

  if (rows.length === 0) {
    throw new ExportError(
      "la exportación quedó vacía (0 juegos): no se escribe un archivo vacío que parecería una biblioteca completa.\n" +
        "Abre Heroic, refresca la biblioteca de la tienda y vuelve a exportar. Si Heroic no te deja refrescar, " +
        "inicia sesión en esa tienda dentro de Heroic primero.",
    );
  }
  if (rows.length > MAX_GAMES) {
    throw new ExportError(
      `la exportación trae ${rows.length} juegos y el máximo aceptado es ${MAX_GAMES}. ` +
        "DealExt rechazaría el archivo; exporta una sola tienda con --stores.",
    );
  }

  rows.sort(compareRows);

  const { realOutput } = await resolveOutputTarget({ ...options, dir: configDir });
  const body = serializeEnvelope(buildEnvelope(rows));

  try {
    await writeFile(realOutput, body, { encoding: "utf8", flag: options.force ? "w" : "wx" });
  } catch (error) {
    if (error.code === "EEXIST") {
      throw new ExportError(`la salida ya existe: ${realOutput}. Usa --force para sobrescribirla o elige otra ruta con --output.`);
    }
    if (error.code === "ENOENT") {
      throw new ExportError(`la carpeta de salida no existe: ${path.dirname(realOutput)}. Créala o elige otra ruta con --output.`);
    }
    throw new ExportError(`no se pudo escribir ${realOutput}: ${error.message}`);
  }

  return {
    outputPath: realOutput,
    bytes: Buffer.byteLength(body, "utf8"),
    total: rows.length,
    duplicates,
    stores: storeStats,
  };
}

// Todo el diagnóstico va a stderr para que stdout quede libre: el único JSON es el archivo.
export function formatReport(summary, now = Date.now()) {
  const lines = [];
  for (const store of summary.stores) {
    lines.push(
      `[heroic-export] ${store.store}: ${plural(store.count, "juego", "juegos")}, ` +
        `${plural(store.installed, "instalado", "instalados")} (caché ${store.cachePath})`,
    );
    if (store.count === 0) {
      lines.push(
        `[heroic-export] aviso: ${store.store} no trajo ningún juego. Refresca esa biblioteca en Heroic ` +
          "antes de fiarte de este export; un caché vacío no prueba que la cuenta esté vacía.",
      );
    }
    const ageMs = now - store.refreshedAt.getTime();
    if (ageMs > STALE_CACHE_WARNING_DAYS * DAY_MS) {
      const days = Math.floor(ageMs / DAY_MS);
      const source = store.refreshedFrom === "timestamp" ? "marca de Heroic" : "fecha del archivo";
      lines.push(
        `[heroic-export] aviso: el caché de ${store.store} lleva sin refrescar ${plural(days, "día", "días")} ` +
          `(${source}). Abre Heroic, pulsa Actualizar biblioteca y vuelve a exportar: Heroic no refresca solo.`,
      );
    }
  }
  if (summary.duplicates > 0) {
    lines.push(`[heroic-export] duplicados omitidos: ${summary.duplicates} (se conserva la primera fila de cada tienda+id)`);
  }
  lines.push(
    "[heroic-export] recordatorio: esto refleja el último refresco de Heroic, no es una consulta en vivo " +
      "y no certifica tu posesión actual. Revisa los conteos antes de subir el archivo.",
  );
  lines.push(
    `[heroic-export] escrito ${summary.outputPath} (${summary.bytes} bytes, ${plural(summary.total, "juego", "juegos")})`,
  );
  return `${lines.join("\n")}\n`;
}

async function main(argv) {
  try {
    const options = parseArgs(argv);
    if (options.help) {
      process.stdout.write(HELP);
      return 0;
    }
    const summary = await runExport(options);
    process.stderr.write(formatReport(summary));
    return 0;
  } catch (error) {
    if (error instanceof ExportError) {
      process.stderr.write(`[heroic-export] ${error.message}\n`);
      if (error.exitCode === 2) process.stderr.write(`\n${HELP}`);
      return error.exitCode;
    }
    process.stderr.write(`[heroic-export] error inesperado: ${error?.stack ?? error}\n`);
    return 1;
  }
}

function isDirectInvocation() {
  if (!process.argv[1]) return false;
  try {
    return realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
}

if (isDirectInvocation()) {
  main(process.argv.slice(2)).then((code) => {
    process.exitCode = code;
  });
}
