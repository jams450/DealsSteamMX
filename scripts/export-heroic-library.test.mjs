// Pruebas del exportador de Heroic. Solo `node --test` y built-ins de Node: este archivo crea
// configuraciones sintéticas de Heroic en un directorio temporal (nunca toca una instalación real ni
// datos personales) y habla con el script por su interfaz pública: argumentos, código de salida,
// stderr y el archivo de salida.
//
// Ejecuta: node --test scripts/export-heroic-library.test.mjs

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, symlink, utimes, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  DEFAULT_OUTPUT,
  MAX_GAMES,
  MAX_OUTPUT_BYTES,
  STALE_CACHE_WARNING_DAYS,
  serializeEnvelope,
} from "./export-heroic-library.mjs";

const SCRIPT = fileURLToPath(new URL("./export-heroic-library.mjs", import.meta.url));
const ISO_UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

// Fixtures con la forma que Heroic escribe en `store_cache`: objetos `GameInfo` completos, con arte,
// rutas de instalación y `runner`. El exportador debe quedarse solo con id, título y estado.
const EPIC_GAMES = [
  {
    runner: "legendary",
    app_name: "Ginger",
    title: "Fortnite",
    is_installed: true,
    install: { install_path: "C:\\Games\\Fortnite", platform: "windows" },
    art_cover: "https://cdn.example/fortnite.png",
    folder_name: "Fortnite",
    save_path: "C:\\Users\\alguien\\saves",
  },
  {
    runner: "legendary",
    app_name: "Sugar",
    title: "Juego Desinstalado",
    is_installed: false,
    install: {},
    art_cover: "https://cdn.example/sugar.png",
  },
];

const GOG_GAMES = [
  {
    runner: "gog",
    app_name: "1207658924",
    title: "The Witcher 3",
    is_installed: true,
    install: { install_path: "/home/alguien/Games/Witcher3", platform: "linux" },
    art_cover: "https://cdn.example/w3.png",
    folder_name: "The Witcher 3",
  },
  {
    runner: "gog",
    app_name: "1423049311",
    title: "Cyberpunk 2077",
    is_installed: false,
    install: {},
    art_cover: "https://cdn.example/cp2077.png",
  },
];

// Orden esperado: tienda ascendente (`epic` < `gog`) y, dentro de cada tienda, id ascendente por
// unidad de código. Ninguna fila del caché se reordena por título.
const EXPECTED_GAMES = [
  { store: "epic", storeGameId: "Ginger", title: "Fortnite", isInstalled: true },
  { store: "epic", storeGameId: "Sugar", title: "Juego Desinstalado", isInstalled: false },
  { store: "gog", storeGameId: "1207658924", title: "The Witcher 3", isInstalled: true },
  { store: "gog", storeGameId: "1423049311", title: "Cyberpunk 2077", isInstalled: false },
];

async function makeTempDir(label) {
  // El nombre lleva espacios a propósito: en Windows la carpeta real de Heroic (`AppData\Roaming\
  // heroic`) y muchas bibliotecas viven bajo rutas con espacios, y un script que no las cite bien
  // falla solo ahí.
  return mkdtemp(path.join(os.tmpdir(), `dealext heroic ${label} `));
}

async function writeCache(configDir, fileName, payload) {
  const cacheDir = path.join(configDir, "store_cache");
  await mkdir(cacheDir, { recursive: true });
  const target = path.join(cacheDir, fileName);
  await writeFile(target, JSON.stringify(payload, null, 2), "utf8");
  return target;
}

async function makeHeroicConfig(options = {}) {
  const {
    epicGames = EPIC_GAMES,
    gogGames = GOG_GAMES,
    epicTimestamp = () => new Date().toString(),
    gogTimestamp = () => new Date().toString(),
    writeEpic = true,
    writeGog = true,
    extraFiles = {},
  } = options;

  const root = await makeTempDir("config");
  const configDir = path.join(root, "Heroic config");
  await mkdir(path.join(configDir, "store_cache"), { recursive: true });

  const epicCache = writeEpic
    ? await writeCache(configDir, "legendary_library.json", {
        library: epicGames,
        ...(epicTimestamp ? { "__timestamp.library": epicTimestamp() } : {}),
      })
    : null;

  const gogCache = writeGog
    ? await writeCache(configDir, "gog_library.json", {
        games: gogGames,
        ...(gogTimestamp ? { "__timestamp.games": gogTimestamp() } : {}),
      })
    : null;

  for (const [relative, content] of Object.entries(extraFiles)) {
    const target = path.join(configDir, relative);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, content, "utf8");
  }

  return { root, configDir, epicCache, gogCache };
}

function runExport(args, { cwd } = {}) {
  const result = spawnSync(process.execPath, [SCRIPT, ...args], {
    cwd: cwd ?? process.cwd(),
    encoding: "utf8",
  });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

async function readExport(filePath) {
  const raw = await readFile(filePath, "utf8");
  return { raw, envelope: JSON.parse(raw) };
}

test("exporta Epic y GOG del caché sintético con el sobre exacto de L4", async () => {
  const { root, configDir } = await makeHeroicConfig();
  const output = path.join(root, "dealext-heroic-library.json");

  const run = runExport(["--dir", configDir, "--output", output]);

  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout, "", "stdout no debe llevar JSON ni logs: el JSON va solo al archivo");

  const { envelope } = await readExport(output);
  assert.deepEqual(Object.keys(envelope), ["format", "version", "source", "exportedAt", "games"]);
  assert.equal(envelope.format, "dealext-library");
  assert.equal(envelope.version, 1);
  assert.equal(envelope.source, "heroic");
  assert.match(envelope.exportedAt, ISO_UTC);
  assert.deepEqual(envelope.games, EXPECTED_GAMES);
  for (const game of envelope.games) {
    assert.deepEqual(Object.keys(game), ["store", "storeGameId", "title", "isInstalled"]);
    assert.equal(typeof game.isInstalled, "boolean");
  }

  // Control positivo de los contadores por tienda y de la limitación del caché.
  assert.match(run.stderr, /epic: 2 juegos, 1 instalado/);
  assert.match(run.stderr, /gog: 2 juegos, 1 instalado/);
  assert.match(run.stderr, /no es una consulta en vivo/);
});

test("no filtra rutas de instalación, arte ni tokens: solo los campos del sobre", async () => {
  const { root, configDir } = await makeHeroicConfig({
    extraFiles: {
      // Señuelos: archivos de credenciales que el exportador nunca debe abrir ni citar.
      "legendary/user.json": '{"token":"SECRET_TOKEN_DECOY"}',
      "store_cache/gog_auth.json": '{"access_token":"SECRET_GOG_TOKEN_DECOY"}',
    },
  });
  const output = path.join(root, "out.json");

  const run = runExport(["--dir", configDir, "--output", output]);
  assert.equal(run.status, 0, run.stderr);

  const { raw } = await readExport(output);
  for (const leaked of [
    "install_path",
    "install",
    "folder_name",
    "save_path",
    "art_cover",
    "art_square",
    "C:\\\\Games",
    "/home/alguien",
    "cdn.example",
    "SECRET_TOKEN_DECOY",
    "SECRET_GOG_TOKEN_DECOY",
    "token",
    "runner",
  ]) {
    assert.equal(raw.includes(leaked), false, `el export no debe contener ${leaked}`);
  }
});

test("un caché con JSON inválido falla cerrado y no escribe salida", async () => {
  const { root, configDir, gogCache } = await makeHeroicConfig();
  await writeFile(gogCache, "{ esto no es json", "utf8");
  const output = path.join(root, "out.json");

  const run = runExport(["--dir", configDir, "--output", output]);

  assert.equal(run.status, 1);
  assert.equal(existsSync(output), false);
  assert.match(run.stderr, /gog_library\.json/);
  assert.match(run.stderr, /JSON/);
});

test("un caché seleccionado ausente falla con mensaje accionable", async () => {
  const { root, configDir } = await makeHeroicConfig({ writeGog: false });

  const run = runExport(["--dir", configDir, "--output", path.join(root, "out.json")]);

  assert.equal(run.status, 1);
  assert.match(run.stderr, /falta el caché de gog/);
  assert.match(run.stderr, /gog_library\.json/);
  assert.match(run.stderr, /Heroic/);
  assert.equal(existsSync(path.join(root, "out.json")), false);
});

test("--stores epic exporta sin exigir el caché de GOG", async () => {
  const { root, configDir } = await makeHeroicConfig({ writeGog: false });
  const output = path.join(root, "solo-epic.json");

  const run = runExport(["--dir", configDir, "--stores", "epic", "--output", output]);

  assert.equal(run.status, 0, run.stderr);
  const { envelope } = await readExport(output);
  assert.deepEqual(
    envelope.games.map((game) => game.store),
    ["epic", "epic"],
  );
  assert.equal(run.stderr.includes("gog:"), false);
});

test("un total vacío no se exporta como si fuera una biblioteca completa", async () => {
  const { root, configDir } = await makeHeroicConfig({
    epicGames: [],
    gogGames: [],
    epicTimestamp: null,
    gogTimestamp: null,
  });
  const output = path.join(root, "out.json");

  const run = runExport(["--dir", configDir, "--output", output]);

  assert.equal(run.status, 1);
  assert.match(run.stderr, /0 juegos|vacía|vacío/);
  assert.equal(existsSync(output), false);
});

test("filas malformadas fallan cerrado: is_installed ausente, título vacío, runner ajeno", async () => {
  const cases = [
    {
      label: "is_installed ausente",
      games: [{ runner: "legendary", app_name: "Ginger", title: "Fortnite" }],
      expected: /is_installed/,
    },
    {
      label: "is_installed no booleano",
      games: [{ runner: "legendary", app_name: "Ginger", title: "Fortnite", is_installed: "true" }],
      expected: /is_installed/,
    },
    {
      label: "título vacío",
      games: [{ runner: "legendary", app_name: "Ginger", title: "   ", is_installed: true }],
      expected: /título/,
    },
    {
      label: "app_name ausente",
      games: [{ runner: "legendary", title: "Fortnite", is_installed: true }],
      expected: /app_name/,
    },
    {
      label: "runner ajeno",
      games: [{ runner: "gog", app_name: "Ginger", title: "Fortnite", is_installed: true }],
      expected: /runner/,
    },
    {
      label: "id de GOG no numérico",
      games: [{ runner: "gog", app_name: "witcher-3", title: "The Witcher 3", is_installed: true }],
      expected: /numérico/,
    },
  ];

  for (const testCase of cases) {
    const isEpic = testCase.label !== "id de GOG no numérico";
    const config = await makeHeroicConfig(
      isEpic
        ? { epicGames: testCase.games, gogGames: GOG_GAMES }
        : { gogGames: testCase.games, epicGames: EPIC_GAMES },
    );
    const output = path.join(config.root, "out.json");
    const run = runExport([
      "--dir",
      config.configDir,
      "--stores",
      isEpic ? "epic" : "gog",
      "--output",
      output,
    ]);

    assert.equal(run.status, 1, `${testCase.label}: se esperaba fallo`);
    assert.match(run.stderr, testCase.expected, `${testCase.label}: mensaje inesperado`);
    assert.equal(existsSync(output), false, `${testCase.label}: no debe escribir salida`);
  }
});

test("duplicados de tienda+id conservan la primera fila y se reportan", async () => {
  const { root, configDir } = await makeHeroicConfig({
    epicGames: [
      { runner: "legendary", app_name: "Ginger", title: "Primera", is_installed: true },
      { runner: "legendary", app_name: "Ginger", title: "Segunda", is_installed: false },
      { runner: "legendary", app_name: "Sugar", title: "Otra", is_installed: false },
    ],
    gogGames: [],
  });
  const output = path.join(root, "out.json");

  const run = runExport(["--dir", configDir, "--stores", "epic", "--output", output]);

  assert.equal(run.status, 0, run.stderr);
  const { envelope } = await readExport(output);
  assert.deepEqual(
    envelope.games.map((game) => [game.storeGameId, game.title, game.isInstalled]),
    [
      ["Ginger", "Primera", true],
      ["Sugar", "Otra", false],
    ],
  );
  assert.match(run.stderr, /duplicados omitidos: 1/);
});

test("el mismo id en tiendas distintas no es duplicado", async () => {
  const { root, configDir } = await makeHeroicConfig({
    epicGames: [{ runner: "legendary", app_name: "1207658924", title: "Epic", is_installed: true }],
    gogGames: [{ runner: "gog", app_name: "1207658924", title: "GOG", is_installed: true }],
  });
  const output = path.join(root, "out.json");

  const run = runExport(["--dir", configDir, "--output", output]);

  assert.equal(run.status, 0, run.stderr);
  const { envelope } = await readExport(output);
  assert.equal(envelope.games.length, 2);
});

test("una ruta con espacios y acentos se cita correctamente", async () => {
  const root = await makeTempDir("carpeta con ñ y espacios");
  const configDir = path.join(root, "Heroic Config Ñandú");
  await mkdir(path.join(configDir, "store_cache"), { recursive: true });
  await writeCache(configDir, "legendary_library.json", { library: EPIC_GAMES });
  await writeCache(configDir, "gog_library.json", { games: GOG_GAMES });
  const output = path.join(root, "salida con espacios", "biblioteca.json");
  await mkdir(path.dirname(output), { recursive: true });

  const run = runExport(["--dir", configDir, "--output", output]);

  assert.equal(run.status, 0, run.stderr);
  const { envelope } = await readExport(output);
  assert.equal(envelope.games.length, 4);
});

test("sin --output escribe dealext-heroic-library.json en el directorio actual", async () => {
  const { configDir, root } = await makeHeroicConfig();
  const cwd = path.join(root, "cwd");
  await mkdir(cwd, { recursive: true });

  const run = runExport(["--dir", configDir], { cwd });

  assert.equal(run.status, 0, run.stderr);
  assert.equal(existsSync(path.join(cwd, DEFAULT_OUTPUT)), true);
});

test("no sobrescribe la salida existente sin --force, y sí con --force", async () => {
  const { root, configDir } = await makeHeroicConfig();
  const output = path.join(root, "out.json");
  await writeFile(output, "contenido previo", "utf8");

  const blocked = runExport(["--dir", configDir, "--output", output]);
  assert.equal(blocked.status, 1);
  assert.match(blocked.stderr, /ya existe/);
  assert.match(blocked.stderr, /--force/);
  assert.equal(await readFile(output, "utf8"), "contenido previo");

  const forced = runExport(["--dir", configDir, "--output", output, "--force"]);
  assert.equal(forced.status, 0, forced.stderr);
  const { envelope } = await readExport(output);
  assert.equal(envelope.source, "heroic");
});

test("rechaza escribir sobre un caché de entrada y deja el caché intacto", async () => {
  const { root, configDir, gogCache } = await makeHeroicConfig();
  const before = await readFile(gogCache, "utf8");

  const run = runExport(["--dir", configDir, "--output", gogCache, "--force"]);

  assert.equal(run.status, 1);
  assert.match(run.stderr, /caché/);
  assert.equal(await readFile(gogCache, "utf8"), before);
  assert.equal(existsSync(path.join(root, "dealext-heroic-library.json")), false);
});

test(
  "rechaza una salida que es symlink a un caché",
  { skip: process.platform === "win32" ? "crear symlinks requiere privilegios en Windows" : false },
  async () => {
    const { root, configDir, gogCache } = await makeHeroicConfig();
    const before = await readFile(gogCache, "utf8");
    const link = path.join(root, "enlace.json");
    await symlink(gogCache, link);

    const run = runExport(["--dir", configDir, "--output", link, "--force"]);

    assert.equal(run.status, 1);
    assert.match(run.stderr, /caché/);
    assert.equal(await readFile(gogCache, "utf8"), before);
  },
);

test("rechaza escribir dentro de store_cache aunque el nombre sea nuevo", async () => {
  const { configDir } = await makeHeroicConfig();
  const output = path.join(configDir, "store_cache", "export-nuevo.json");

  const run = runExport(["--dir", configDir, "--output", output]);

  assert.equal(run.status, 1);
  assert.match(run.stderr, /store_cache/);
  assert.equal(existsSync(output), false);
});

test("nunca escribe dentro de la configuración de Heroic: las credenciales quedan intactas", async () => {
  const authBody = '{"access_token":"SECRET_GOG_TOKEN_DECOY"}';
  const { root, configDir } = await makeHeroicConfig({
    extraFiles: {
      "gog_store/auth.json": authBody,
      "legendary/user.json": '{"token":"SECRET_TOKEN_DECOY"}',
    },
  });
  const authPath = path.join(configDir, "gog_store", "auth.json");
  const userPath = path.join(configDir, "legendary", "user.json");

  // Archivo existente: --force no debe poder convertirlo en la salida.
  const overwrite = runExport(["--dir", configDir, "--output", authPath, "--force"]);
  assert.equal(overwrite.status, 1);
  assert.match(overwrite.stderr, /configuración de Heroic/);
  assert.match(overwrite.stderr, /--output/);
  assert.equal(await readFile(authPath, "utf8"), authBody);

  // Archivo nuevo dentro de la configuración: tampoco se crea.
  const fresh = path.join(configDir, "gog_store", "export-nuevo.json");
  const create = runExport(["--dir", configDir, "--output", fresh]);
  assert.equal(create.status, 1);
  assert.equal(existsSync(fresh), false);

  // Y el archivo de sesión de Epic sigue igual tras intentarlo también.
  const epicAttempt = runExport(["--dir", configDir, "--output", userPath, "--force"]);
  assert.equal(epicAttempt.status, 1);
  assert.equal(await readFile(userPath, "utf8"), '{"token":"SECRET_TOKEN_DECOY"}');

  // La exportación normal a una ruta fuera de la configuración sigue funcionando y no toca nada de eso.
  const ok = runExport(["--dir", configDir, "--output", path.join(root, "fuera.json")]);
  assert.equal(ok.status, 0, ok.stderr);
  assert.equal(await readFile(authPath, "utf8"), authBody);
});

test(
  "rechaza una salida que llega a la configuración por un symlink de directorio",
  { skip: process.platform === "win32" ? "crear symlinks requiere privilegios en Windows" : false },
  async () => {
    const authBody = '{"access_token":"SECRET_GOG_TOKEN_DECOY"}';
    const { root, configDir } = await makeHeroicConfig({
      extraFiles: { "gog_store/auth.json": authBody },
    });
    // El camino literal está fuera de la configuración; el realpath cae dentro.
    const mirror = path.join(root, "mirror-config");
    await symlink(configDir, mirror);
    const disguised = path.join(mirror, "gog_store", "auth.json");

    const run = runExport(["--dir", configDir, "--output", disguised, "--force"]);

    assert.equal(run.status, 1);
    assert.match(run.stderr, /configuración de Heroic/);
    assert.equal(await readFile(path.join(configDir, "gog_store", "auth.json"), "utf8"), authBody);
  },
);

test("un id numérico de GOG de 64 dígitos es válido y uno de 65 no", async () => {
  const id64 = "1".repeat(64);
  const accepted = await makeHeroicConfig({
    epicGames: [],
    gogGames: [{ runner: "gog", app_name: id64, title: "Id largo", is_installed: true }],
  });
  const acceptedOutput = path.join(accepted.root, "ok.json");
  const acceptedRun = runExport([
    "--dir",
    accepted.configDir,
    "--stores",
    "gog",
    "--output",
    acceptedOutput,
  ]);
  assert.equal(acceptedRun.status, 0, acceptedRun.stderr);
  const { envelope } = await readExport(acceptedOutput);
  assert.equal(envelope.games[0].storeGameId, id64);

  const id65 = "1".repeat(65);
  const refused = await makeHeroicConfig({
    epicGames: [],
    gogGames: [{ runner: "gog", app_name: id65, title: "Id demasiado largo", is_installed: true }],
  });
  const refusedOutput = path.join(refused.root, "largo.json");
  const refusedRun = runExport([
    "--dir",
    refused.configDir,
    "--stores",
    "gog",
    "--output",
    refusedOutput,
  ]);
  assert.equal(refusedRun.status, 1);
  assert.match(refusedRun.stderr, /64/);
  assert.equal(existsSync(refusedOutput), false);
});

test("advierte cuando el caché está viejo y calla cuando está fresco", async () => {
  const stale = new Date(Date.now() - (STALE_CACHE_WARNING_DAYS + 5) * 24 * 60 * 60 * 1000);
  const { root, configDir } = await makeHeroicConfig({
    epicTimestamp: () => stale.toString(),
    gogTimestamp: () => new Date().toString(),
  });
  const output = path.join(root, "viejo.json");

  const run = runExport(["--dir", configDir, "--output", output]);
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stderr, /sin refrescar/);
  assert.match(run.stderr, /epic/);
  assert.equal(/sin refrescar[^\n]*gog/.test(run.stderr), false);

  const fresh = await makeHeroicConfig();
  const freshRun = runExport(["--dir", fresh.configDir, "--output", path.join(fresh.root, "ok.json")]);
  assert.equal(freshRun.status, 0, freshRun.stderr);
  assert.equal(freshRun.stderr.includes("sin refrescar"), false);
});

test("sin marca de refresco usa la fecha del archivo para la advertencia", async () => {
  const { root, configDir, epicCache } = await makeHeroicConfig({ epicTimestamp: null, gogTimestamp: null });
  const old = new Date(Date.now() - (STALE_CACHE_WARNING_DAYS + 30) * 24 * 60 * 60 * 1000);
  await utimes(epicCache, old, old);

  const run = runExport(["--dir", configDir, "--output", path.join(root, "mtime.json")]);

  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stderr, /sin refrescar/);
  assert.match(run.stderr, /epic/);
});

test("argumentos inválidos salen con código 2 y no tocan el disco", async () => {
  const noDir = runExport([]);
  assert.equal(noDir.status, 2);
  assert.match(noDir.stderr, /--dir/);

  const { configDir, root } = await makeHeroicConfig();
  const badStore = runExport(["--dir", configDir, "--stores", "epic,steam", "--output", path.join(root, "x.json")]);
  assert.equal(badStore.status, 2);
  assert.match(badStore.stderr, /store/);
  assert.equal(existsSync(path.join(root, "x.json")), false);

  const badFlag = runExport(["--dir", configDir, "--nope"]);
  assert.equal(badFlag.status, 2);
  assert.match(badFlag.stderr, /--nope/);

  const emptyStores = runExport(["--dir", configDir, "--stores", " , "]);
  assert.equal(emptyStores.status, 2);
});

test("--help sale con código 0 y documenta el contrato", async () => {
  const run = runExport(["--help"]);

  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /--dir/);
  assert.match(run.stdout, /--stores/);
  assert.match(run.stdout, /--output/);
  assert.match(run.stdout, /--force/);
});

test("el tope de conteo es 10000 filas y rechaza una fila más", async () => {
  const many = Array.from({ length: MAX_GAMES + 1 }, (_, index) => ({
    runner: "legendary",
    app_name: `App${index}`,
    title: `Juego ${index}`,
    is_installed: false,
  }));
  const { root, configDir } = await makeHeroicConfig({ epicGames: many, gogGames: [] });
  const output = path.join(root, "grande.json");

  const run = runExport(["--dir", configDir, "--stores", "epic", "--output", output]);

  assert.equal(run.status, 1);
  assert.match(run.stderr, /10000/);
  assert.equal(existsSync(output), false);
});

test("respeta los topes por campo del import: id ≤ 64 y título ≤ 256", async () => {
  const longId = "A".repeat(65);
  const longTitle = "T".repeat(257);

  const idCase = await makeHeroicConfig({
    epicGames: [{ runner: "legendary", app_name: longId, title: "Ok", is_installed: true }],
    gogGames: [],
  });
  const idOutput = path.join(idCase.root, "id.json");
  const idRun = runExport(["--dir", idCase.configDir, "--stores", "epic", "--output", idOutput]);
  assert.equal(idRun.status, 1);
  assert.match(idRun.stderr, /64/);
  assert.equal(existsSync(idOutput), false);

  const titleCase = await makeHeroicConfig({
    epicGames: [{ runner: "legendary", app_name: "Ginger", title: longTitle, is_installed: true }],
    gogGames: [],
  });
  const titleOutput = path.join(titleCase.root, "title.json");
  const titleRun = runExport(["--dir", titleCase.configDir, "--stores", "epic", "--output", titleOutput]);
  assert.equal(titleRun.status, 1);
  assert.match(titleRun.stderr, /256/);
  assert.equal(existsSync(titleOutput), false);
});

test("el tope de bytes es 10 MiB y el guard corta antes de escribir", () => {
  assert.equal(MAX_OUTPUT_BYTES, 10 * 1024 * 1024);

  const envelope = {
    format: "dealext-library",
    version: 1,
    source: "heroic",
    exportedAt: "2026-10-18T00:00:00.000Z",
    games: EXPECTED_GAMES,
  };

  // El guard real no se alcanza con 10 000 filas válidas (id ≤ 64 y título ≤ 256), así que se
  // prueba el mismo guard con un tope inyectado.
  assert.throws(() => serializeEnvelope(envelope, 64), /10 MiB|bytes|supera/i);
  assert.equal(typeof serializeEnvelope(envelope), "string");
});


test("omite únicamente el componente interno gog-redist sin perder juegos", async () => {
  const redist = { runner: "gog", app_name: "gog-redist", title: "Galaxy Common Redistributables", is_installed: true, install: { is_dlc: true } };
  const { root, configDir } = await makeHeroicConfig({ gogGames: [redist, ...GOG_GAMES] });
  const output = path.join(root, "library.json");
  const run = runExport(["--dir", configDir, "--output", output]);
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout, "");
  assert.deepEqual((await readExport(output)).envelope.games, EXPECTED_GAMES);
  assert.match(run.stderr, /gog-redist/);

  const only = await makeHeroicConfig({ gogGames: [redist], writeEpic: false });
  const emptyOutput = path.join(only.root, "empty.json");
  const empty = runExport(["--dir", only.configDir, "--stores", "gog", "--output", emptyOutput]);
  assert.equal(empty.status, 1);
  assert.equal(existsSync(emptyOutput), false);
});
