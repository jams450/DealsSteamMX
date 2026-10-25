import test from "node:test";
import assert from "node:assert/strict";
import {
  HEROIC_LIBRARY_EXPORT_FORMAT,
  HEROIC_LIBRARY_EXPORT_MAX_BYTES,
  HEROIC_LIBRARY_EXPORT_MAX_GAMES,
  HEROIC_LIBRARY_EXPORT_SOURCE,
  HEROIC_LIBRARY_EXPORT_VERSION,
  parseHeroicLibraryExport,
  type HeroicLibraryExport
} from "./heroic-library-export.ts";

// Un sobre válido mínimo. Las pruebas lo copian y rompen una sola cosa por caso, para que el fallo diga
// exactamente qué regla se perdió.
function envelope(overrides: Record<string, unknown> = {}, games?: unknown[]) {
  return {
    format: "dealext-library",
    version: 1,
    source: "heroic",
    exportedAt: "2026-10-02T18:04:05Z",
    games: games ?? [
      { store: "epic", storeGameId: "Kinglet", title: "Floppy Knights", isInstalled: true },
      { store: "gog", storeGameId: "1423049311", title: "Cyberpunk 2077", isInstalled: false }
    ],
    ...overrides
  };
}

function parse(input: unknown): HeroicLibraryExport | null {
  return parseHeroicLibraryExport(input);
}

test("heroic: el contrato congelado coincide con el backend", () => {
  assert.equal(HEROIC_LIBRARY_EXPORT_FORMAT, "dealext-library");
  assert.equal(HEROIC_LIBRARY_EXPORT_VERSION, 1);
  assert.equal(HEROIC_LIBRARY_EXPORT_SOURCE, "heroic");
  assert.equal(HEROIC_LIBRARY_EXPORT_MAX_BYTES, 10 * 1024 * 1024);
  assert.equal(HEROIC_LIBRARY_EXPORT_MAX_GAMES, 10_000);
});

test("heroic: un sobre válido se acepta y se normaliza", () => {
  const parsed = parse(envelope());
  assert.notEqual(parsed, null);
  assert.equal(parsed?.format, "dealext-library");
  assert.equal(parsed?.version, 1);
  assert.equal(parsed?.source, "heroic");
  assert.equal(parsed?.exportedAt, "2026-10-02T18:04:05.000Z");
  assert.equal(parsed?.games.length, 2);
  assert.equal(parsed?.games[0]?.store, "epic");
  assert.equal(parsed?.games[0]?.storeGameId, "Kinglet");
  assert.equal(parsed?.games[0]?.isInstalled, true);
  assert.equal(parsed?.games[1]?.store, "gog");
  assert.equal(parsed?.games[1]?.isInstalled, false);
});

test("heroic: tienda, id y título se recortan y la tienda se pasa a minúsculas", () => {
  const parsed = parse(
    envelope({}, [{ store: " GOG ", storeGameId: "1423049311", title: " Cyberpunk 2077 ", isInstalled: true }])
  );
  assert.equal(parsed?.games[0]?.store, "gog");
  assert.equal(parsed?.games[0]?.storeGameId, "1423049311");
  assert.equal(parsed?.games[0]?.title, "Cyberpunk 2077");
});

test("heroic: el id de tienda no se recorta en silencio; un id con espacios o control se rechaza", () => {
  // La tienda y el título son vocabulario y texto de pantalla; el id es la llave que se escribe, así que se
  // valida tal como llega. Mismo criterio que el backend, que también lo rechaza en vez de recortarlo.
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: " Kinglet", title: "Juego", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "Kinglet ", title: "Juego", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "King\tlet", title: "Juego", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "King\u0000let", title: "Juego", isInstalled: true }])), null);
  assert.equal(
    parse(envelope({}, [{ store: "epic", storeGameId: "Kinglet Two", title: "Juego", isInstalled: true }]))?.games[0]
      ?.storeGameId,
    "Kinglet Two"
  );
});

test("heroic: un sobre de otro producto o versión se rechaza", () => {
  assert.equal(parse(envelope({ format: "playnite" })), null);
  assert.equal(parse(envelope({ format: "DEALEXT-LIBRARY" })), null);
  assert.equal(parse(envelope({ format: undefined })), null);
  assert.equal(parse(envelope({ version: 2 })), null);
  assert.equal(parse(envelope({ version: "1" })), null);
  assert.equal(parse(envelope({ version: undefined })), null);
  assert.equal(parse(envelope({ source: "playnite" })), null);
  assert.equal(parse(envelope({ source: undefined })), null);
  assert.equal(parse(null), null);
  assert.equal(parse([envelope()]), null);
  assert.equal(parse("texto"), null);
});

test("heroic: exportedAt es obligatorio, ISO y en UTC", () => {
  assert.equal(parse(envelope({ exportedAt: undefined })), null);
  assert.equal(parse(envelope({ exportedAt: "" })), null);
  assert.equal(parse(envelope({ exportedAt: "ayer" })), null);
  assert.equal(parse(envelope({ exportedAt: "2026-02-30T00:00:00Z" })), null);
  // La envoltura promete UTC: un desfase local se rechaza en vez de almacenarse desplazado.
  assert.equal(parse(envelope({ exportedAt: "2026-10-02T18:04:05+02:00" })), null);
  assert.equal(parse(envelope({ exportedAt: "2026-10-02T18:04:05-06:00" })), null);
  // Y tampoco se asume la zona: sin "Z", solo la fecha, o con separador de espacio tampoco es el contrato.
  assert.equal(parse(envelope({ exportedAt: "2026-10-02T18:04:05" })), null);
  assert.equal(parse(envelope({ exportedAt: "2026-10-02" })), null);
  assert.equal(parse(envelope({ exportedAt: "2026-10-02 18:04:05Z" })), null);
  assert.equal(parse(envelope({ exportedAt: "02/10/2026 18:04:05" })), null);
  assert.equal(parse(envelope({ exportedAt: "2026-10-02T18:04:05.1234567Z" })), null);
  assert.equal(parse(envelope({ exportedAt: "2026-10-02T18:04+00:00" }))?.exportedAt, "2026-10-02T18:04:00.000Z");
  assert.equal(parse(envelope({ exportedAt: "2026-10-02T18:04:05.123Z" }))?.exportedAt, "2026-10-02T18:04:05.123Z");
});

test("heroic: games debe existir y no estar vacío", () => {
  assert.equal(parse(envelope({ games: undefined })), null);
  assert.equal(parse(envelope({ games: "nope" })), null);
  assert.equal(parse(envelope({ games: [] })), null);
});

test("heroic: el tope de juegos es el mismo que el de la importación de Playnite", () => {
  const atCap = Array.from({ length: HEROIC_LIBRARY_EXPORT_MAX_GAMES }, (_, index) => ({
    store: "epic",
    storeGameId: `app${index}`,
    title: `Juego ${index}`,
    isInstalled: true
  }));
  assert.equal(parse(envelope({}, atCap))?.games.length, HEROIC_LIBRARY_EXPORT_MAX_GAMES);
  assert.equal(parse(envelope({}, [...atCap, atCap[0]])), null);
});

test("heroic: solo epic y gog; cualquier otra tienda rechaza el archivo", () => {
  assert.equal(parse(envelope({}, [{ store: "steam", storeGameId: "730", title: "CS2", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "xbox", storeGameId: "9N", title: "Halo", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "", storeGameId: "K", title: "Juego", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ storeGameId: "K", title: "Juego", isInstalled: true }])), null);
});

test("heroic: storeGameId y title son obligatorios y están acotados", () => {
  assert.equal(parse(envelope({}, [{ store: "epic", title: "Juego", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "   ", title: "Juego", isInstalled: true }])), null);
  assert.equal(
    parse(envelope({}, [{ store: "epic", storeGameId: "a".repeat(65), title: "Juego", isInstalled: true }])),
    null
  );
  assert.equal(
    parse(envelope({}, [{ store: "epic", storeGameId: "Kinglet", title: "t".repeat(257), isInstalled: true }])),
    null
  );
  // El ancho exacto de la columna sí pasa: user_library.store_game_id es VARCHAR(64).
  assert.equal(
    parse(envelope({}, [{ store: "epic", storeGameId: "a".repeat(64), title: "t".repeat(256), isInstalled: true }]))
      ?.games[0]?.storeGameId.length,
    64
  );
});

test("heroic: isInstalled es obligatorio y booleano, nunca se lee como false", () => {
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "K", title: "Juego" }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "K", title: "Juego", isInstalled: null }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "K", title: "Juego", isInstalled: "true" }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "K", title: "Juego", isInstalled: 1 }])), null);
});

test("heroic: un id de GOG no numérico se rechaza; el de Epic es opaco", () => {
  assert.equal(parse(envelope({}, [{ store: "gog", storeGameId: "cyberpunk-2077", title: "Cyberpunk", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "gog", storeGameId: "14230 49311", title: "Cyberpunk", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "gog", storeGameId: "1e9", title: "Cyberpunk", isInstalled: true }])), null);
  assert.equal(parse(envelope({}, [{ store: "epic", storeGameId: "a1b2c3d4e5f6", title: "Juego", isInstalled: true }]))?.games[0]?.storeGameId, "a1b2c3d4e5f6");
  // El ancho de la columna es el tope del id, también para GOG: el exportador recorta dígitos, la puerta no
  // acepta más de 64.
  assert.equal(
    parse(envelope({}, [{ store: "gog", storeGameId: "9".repeat(64), title: "Juego", isInstalled: true }]))?.games[0]
      ?.storeGameId.length,
    64
  );
  assert.equal(parse(envelope({}, [{ store: "gog", storeGameId: "9".repeat(65), title: "Juego", isInstalled: true }])), null);
});

test("heroic: una fila mala después de una buena rechaza el archivo completo", () => {
  assert.equal(
    parse(
      envelope({}, [
        { store: "epic", storeGameId: "Kinglet", title: "Floppy Knights", isInstalled: true },
        { store: "gog", storeGameId: "no-es-numero", title: "Cyberpunk 2077", isInstalled: true }
      ])
    ),
    null
  );
  assert.equal(
    parse(
      envelope({}, [
        { store: "epic", storeGameId: "Kinglet", title: "Floppy Knights", isInstalled: true },
        null
      ])
    ),
    null
  );
});

test("heroic: solo se conservan los campos de la lista blanca", () => {
  const parsed = parse(
    envelope(
      { configPath: "/home/jugador/.config/heroic", token: "secreto" },
      [{ store: "epic", storeGameId: "Kinglet", title: "Floppy Knights", isInstalled: true, art: "https://x/y.jpg" }]
    )
  );
  assert.deepEqual(Object.keys(parsed ?? {}).sort(), ["exportedAt", "format", "games", "source", "version"]);
  assert.deepEqual(Object.keys(parsed?.games[0] ?? {}).sort(), ["isInstalled", "store", "storeGameId", "title"]);
});

test("heroic: los duplicados se conservan aquí, el backend se queda con el primero", () => {
  const parsed = parse(
    envelope({}, [
      { store: "epic", storeGameId: "Kinglet", title: "Floppy Knights", isInstalled: true },
      { store: "epic", storeGameId: "Kinglet", title: "Floppy Knights", isInstalled: false }
    ])
  );
  assert.equal(parsed?.games.length, 2);
  assert.equal(parsed?.games[0]?.isInstalled, true);
});

test("heroic: una fila de Playnite no es una fila de Heroic, y viceversa", () => {
  // El export de Playnite es un arreglo de filas con PluginId/Source/Added, no un sobre. Ninguna de las dos
  // formas puede alimentar la otra puerta: aquí la fila de Playnite no tiene `store`.
  const playniteRow = {
    GameId: "Kinglet",
    PluginId: "00000002-dbd1-46c6-b5d0-b1ba559d10e4",
    Source: "Epic",
    Name: "Floppy Knights",
    IsInstalled: true,
    Added: "/Date(1736000000000)/"
  };
  assert.equal(parse([playniteRow]), null);
  assert.equal(parse({ Games: [playniteRow] }), null);
  assert.equal(parse(envelope({}, [playniteRow])), null);

  // Y un sobre de Heroic tampoco se lee como el reporte de una importación: `source` es "heroic", no un
  // arreglo de filas.
  const parsed = parse(envelope());
  assert.notEqual(parsed, null);
  assert.equal(Array.isArray(parsed), false);
});
