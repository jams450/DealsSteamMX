# DealExt: biblioteca manual de juegos de consola

Estado: **plan**. Fase 0 (validar la fuente de datos, §2) **aprobada el 2026-09-21**: fuente de
enriquecimiento = **IGDB** (§2.3, 5/5 títulos con portada) con Wikidata de respaldo sin credenciales.
Fase 1 (vocabulario de plataforma abierto, §3) **implementada**: `StoreKeys.Normalize` + `normalizeStore`
y los 6 puntos de reseña/posesión del frontend, verificados con build y contratos. Fases 2–5
**implementadas**: identidad `game_id` + outcomes (`created | attached | duplicate | candidates`) con el
lock `dealext.game_identity`; `POST /api/library/manual`, `GET /api/library/manual/enrich` y `DELETE
/api/library/{userLibraryId}` (antes no existía borrado de filas); diálogo «Añadir manual» en la
biblioteca con catálogo de consolas + «Otra plataforma…», búsqueda IGDB con portada elegible y «Deshacer»
del alta. Los candidatos del catálogo **no tienen GET**: viajan dentro de la respuesta del POST, que es
la única llamada que la regla anti-título puede negar (el GET previsto se descartó como código muerto).
Verificado: `dotnet build` 0/0, `pnpm build` ok, `pnpm lint` 0 errores, 73 tests de contratos en verde.
**Sin verificación en vivo** (las filas de §9 siguen pendientes). Fase 6 (carga masiva desde el export de
Playnite, §7) **implementada y verificada por build/contratos**: contrato paralelo
`POST /api/library/console-import/preview` → `…/commit` + diálogo «Importar consolas»; la corrida en vivo
sigue pendiente (el export medido todavía no trae filas de consola ni `Platforms`; §7.6). IGDB se configura con
`IGDB_CLIENT_ID` (+`IGDB_CLIENT_SECRET`/`IGDB_TOKEN`) en `.env` (sin
credenciales, la búsqueda responde «no disponible» y el alta sigue funcionando sin portada).

Alcance: dar de alta juegos que no son de PC —Switch, PlayStation, PSP, DS, etc.— en la biblioteca,
poblando la tabla `games` ya existente y ligando la fila a la **plataforma de consola como si fuera
tienda** (`user_library.store`), con un id asignado cuando no existe ninguno en la BD. Dos puertas: el
alta **a mano** (§5) y la **carga masiva** desde el export de Playnite (§7). La búsqueda/autocompletado
del alta decide la Fase 0.

Sin precios para consola (medido en §2: ITAD no tiene ninguna tienda de consola), sin DDL nuevo y sin
tocar el import de tiendas (`POST /api/library/import` sigue congelado y exigiendo `Source` no nulo).

Continúa a `PLAN_LIBRARY.md` y `PLAN_CATALOG.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Decisión

| Pregunta | Respuesta |
|---|---|
| ¿Entra consola en `user_library`? | **Sí, con `store` = plataforma** (`switch`, `psp`, `nds`…). El vocabulario de `store` ya es el vocabulario compartido con `game_reviews.platform` y con los badges de posesión: la biblioteca no distingue "tienda PC" de "plataforma", distingue `state`. |
| ¿Se puede usar `state='owned'`? | **Sí, sin tocar nada.** `owned` es el estado por defecto de toda entrada que no es wishlist ni suscripción. Wishlist es allowlist positiva `wished` (`PLAN_WISHLIST.md` §7), así que una fila de consola no se cuela en alertas. |
| ¿Hace falta migración? | **No.** `store VARCHAR(32)`, `store_game_id VARCHAR(64)`, `games`, `game_external_ids.namespace VARCHAR(32)`: todos aceptan los valores nuevos sin DDL. En todo el repo no hay ni un `CHECK`. |
| ¿Y el id de consola si no existe en la BD? | No se inventa: se **asigna el `game_id` canónico** como `store_game_id` y como `external_id` (§4). |
| ¿Depende de una fuente externa? | **No para el alta.** El título escrito a mano entra siempre. La fuente solo aporta autocompletado y portada/año, y su validación es la Fase 0 (§2). |

Piezas: vocabulario de plataforma (§3), identidad (§4), endpoint + UI (§5), fuente de datos (§2, primero).

## 2. Fase 0: validar la fuente de datos

Hecho hoy, **solo lectura, endpoints públicos, sin credenciales**, con control positivo y negativo en cada
sonda (regla de `AGENTS.md` «Diagnóstico de un proveedor externo»: una sonda sin control devuelve el vacío
y se confunde con "no existe").

### 2.1 Medidas

| # | Sonda | Resultado | Control |
|---|---|---|---|
| 1 | `wbsearchentities` de las consolas | QIDs: Switch `Q19610114`, DS `Q170323`, PSP `Q170325`, 3DS `Q203597`, PS4 `Q5014725` | El primer resultado de cada búsqueda describe consola («hybrid video game console», «handheld game console», …) y no un juego homónimo |
| 2 | `P400` (plataforma) en 3 títulos conocidos | `Mario Kart DS` → `Q170323` ✓ · `Breath of the Wild` → `Q19610114` ✓ · `Persona 3 Portable` → Windows + `Q170325` ✓ | Los tres QIDs se verificaron contra su label antes de usarlos |
| 3 | Cobertura de portada `P18` | **Switch 113/7788 (1.4%) · DS 38/2046 (1.9%) · PSP 47/1416 (3.3%)** | Tres títulos populares **sin** `P18`: Metroid Dread, Super Mario 64, Crisis Core → corrobora el recuento |
| 4 | Búsqueda por título | La búsqueda **exacta** por `rdfs:label` devuelve 0 filas para `Grand Theft Auto: Vice City Stories`, mientras `wbsearchentities` lo encuentra (`Q94640`). `wbsearchentities` también devuelve basura: la serie (`Q188196 Mario Kart`) y un artículo de The Verge | Filtrar el resultado elegido por `P31` («video game») en la selección, no en cada tecla |
| 5 | Controles negativos | QID inexistente → **0 filas**; título inexistente (`Zelda Floppy Disk Edition`) → **0 filas** | La sonda discrimina: "0 filas" ≠ "mal escrito" |
| 6 | IGDB sin token → **HTTP 401**; RAWG sin key → **HTTP 401** | **No medibles a ciegas** → puerta de decisión en §2.3 | El 401 es el resultado esperado de ambos |
| 7 | ITAD `service/shops/v1?country=MX` | **34 tiendas, ninguna de consola** (Nintendo/PlayStation/console → vacío) | Reproduce el resultado de `PLAN_LIBRARY.md` §3 con el mismo comando publicado allí |
| 8 | Fixture `docs/dealext-library.json` | 2586 filas, **0 con `Source` vacío**: Steam 1168, Epic 394, Xbox 348, GOG 342, Amazon 195, Ubisoft 89, Humble 39, Battle.net 11 → **las consolas no están en el export actual** | — |

### 2.2 Conclusión de la Fase 0

| Fuente | Veredicto | Por qué |
|---|---|---|
| **Wikidata** | Sirve como **fuente de identidad/título/año/plataforma sin credenciales**; **no sirve de portada** (medida 3: 1–3% de cobertura, y los juegos no suben portada a Commons por licencia) | Búsqueda fuzzy medida (medida 4) y controles limpios (medida 5) |
| **IGDB** | **Aprobada** como fuente de **portada + año + plataforma** (sonda §2.3: 5/5 títulos con portada, 9/9 plataformas, controles ✓; CDN de imágenes y `PLAN_CATALOG.md` §6 ya lo nombra como espinazo) | 4 req/s, licencia gratis solo no-comercial; sonda §2.3 |
| **RAWG** | Descartado salvo que quieras pedir key: exige key desde la primera llamada (medida 6) | — |
| **ITAD / tiendas** | **Fuera**: ninguna tienda de consola (medida 7). Consola = «Sin precios vinculados» para siempre en V1 | Mismo resultado que `PLAN_LIBRARY.md` §3 |

### 2.3 Puerta de decisión: la sonda IGDB (**ejecutada 2026-09-21 → APROBADA**)

Intercambio `client_credentials` contra `id.twitch.tv/oauth2/token` → `access_token`, y luego las cuatro
sondas con `Client-ID` + `Authorization: Bearer`. **Los valores no están ni estarán en este documento ni
en el repo**: solo en variables de entorno del ejecutor y, en la Fase 5, en el `.env`/entorno del
contenedor (gitignoreado). Higiene: esta ronda las credenciales pasaron por el chat — si quieres
rotación, regenera el client secret en el panel de la app de Twitch (no es urgente, nunca tocaron disco).

```bash
# 1. CONTROL POSITIVO. Medido: 3 filas — Switch (cover + fecha) y PC con cover, la fila de DS sin cover.
curl -s -X POST "https://api.igdb.com/v4/games" -H "Client-ID: $IGDB_CLIENT_ID" \
  -H "Authorization: Bearer $IGDB_TOKEN" \
  --data 'fields name,first_release_date,platforms.name,cover.url; search "metroid dread"; limit 3;'

# 2. Plataformas — CORREGIDO respecto a la versión original del plan: `where name = *("Switch",...)`
#    devolvía HTTP 400 Syntax Error («Expecting a STRING as input» en `*`); la igualdad exacta funciona.
#    Medido: 9/9 — Switch 130, DS 20, 3DS 37, PSP 38, PS4 48, PS5 167, PS Vita 46, Wii 5, Wii U 41.
#    Control: `where id = 130` → «Nintendo Switch» exacto.
curl -s -X POST "https://api.igdb.com/v4/platforms" -H "Client-ID: $IGDB_CLIENT_ID" \
  -H "Authorization: Bearer $IGDB_TOKEN" \
  --data 'fields name; where name = ("Nintendo Switch","Nintendo DS","Nintendo 3DS","PlayStation Portable","PlayStation 4","PlayStation 5","PlayStation Vita","Wii","Wii U"); limit 30;'

# 3. CONTROL NEGATIVO. Medido: [] (arreglo vacío), no un error.
curl -s -X POST "https://api.igdb.com/v4/games" -H "Client-ID: $IGDB_CLIENT_ID" \
  -H "Authorization: Bearer $IGDB_TOKEN" --data 'search "zelda floppy disk edition"; limit 3;'

# 4. Portada de 5 títulos de la colección. Medido: 5/5 con ≥1 fila con cover.
curl -s -X POST "https://api.igdb.com/v4/games" -H "Client-ID: $IGDB_CLIENT_ID" \
  -H "Authorization: Bearer $IGDB_TOKEN" \
  --data 'fields name,cover.url; where name = ("Persona 3 Portable","Mario Kart DS",
  "Grand Theft Auto: Vice City Stories","Metroid Dread","Crisis Core: Final Fantasy VII"); limit 10;'
```

| Sonda | Resultado (2026-09-21) | Control |
|---|---|---|
| Búsqueda con portada | `metroid dread` → 3 filas; **2 con cover** (Switch con fecha, PC) | Esperaba ≥1 cover ✓ |
| Plataformas de DealExt | **9/9** con sus ids (arriba) | `where id = 130` → «Nintendo Switch» ✓ |
| Título inexistente | `[]`, sin error | — |
| Cobertura de portada | **5/5** (P3P, MKDS, GTA VCS, Metroid Dread, Crisis Core) — contra el **1–3 % de Wikidata** (medida 3) | Misma colección, misma pregunta |

**Dos trampas medidas, para la Fase 5:**

1. **Filas duplicadas por plataforma**: un mismo título devuelve varios juegos (PC/DS/Switch) y una fila
   puede **no traer portada** (la de DS). La selección debe preferir la fila de la plataforma elegida en
   el diálogo; si esa no tiene `cover`, caer a otra fila con cover; si ninguna, `image_url NULL`.
2. **URL protocol-relative**: `//images.igdb.com/...t_thumb/....jpg` → el servidor antepone `https:` (y
   puede cambiar el tamaño). La escribe **solo el servidor**: el navegador nunca habla con IGDB (§5).

**Decisión tomada**: **IGDB = fuente de enriquecimiento** del diálogo de §5 (búsqueda + portada + año +
plataforma). Wikidata queda como respaldo sin credenciales: si IGDB cae, el diálogo sigue dando de alta a
mano y la fila queda sin portada.

**En ningún caso el alta manual espera a esta decisión**: con fuente o sin ella, el usuario escribe el
título y la plataforma y la fila entra. La fuente decide si el diálogo tiene autocompletado con portada.

### 2.4 ¿Hace falta apikey o cuenta?

**No para nada esencial: todo el plan corre free sin ninguna cuenta.**

| Pieza | ¿Cuenta? | ¿Key? | Coste | Estado |
|---|---|---|---|---|
| Alta manual (título + plataforma, sin fuente) | no | no | 0 | — |
| Búsqueda / año / plataforma en **Wikidata** (API + SPARQL) | no | no | 0 | **medido hoy**: HTTP 200 sin credenciales (§2.1) |
| Portada + búsqueda en **IGDB** | sí: cuenta Twitch Developer (gratis, sin tarjeta) | token `client_credentials` gratis, 4 req/s, licencia solo no-comercial | 0 | **aprobada 2026-09-21**: sonda §2.3 (5/5 con portada) |
| **RAWG** | sí | key gratuita con límites | 0 | descartado salvo que quieras pedirla (§2.2) |
| ITAD y Steam (ya existentes) | ya configuradas | `ITAD:ApiKey` ya vive en el entorno del servidor | ya estaba | sin cambios |

Único escenario con cuenta: IGDB, **ya aprobado** (§2.3). Cuenta Twitch gratis, sin tarjeta; client id,
secret y token viven solo en variables de entorno (`.env` gitignoreado o entorno del contenedor) y
**nunca** en el repo ni en esta documentación.

## 3. Plataforma abierta: cualquier consola, sin tocar código

El vocabulario de `user_library.store` queda **abierto para las plataformas** y cerrado solo para las
**tiendas del import**. Cualquier consola entra hoy —`snes`, `3ds`, `steamdeck`, `android`— sin añadir
constantes.

### Regla

| Qué | Vocabulario |
|---|---|
| Tiendas del import | Las 8 canónicas de `StoreKeys` / `STORE_KEYS` (las manda `LibraryController.SourceRules`) — **cerrado** |
| Plataformas (consola) | Slug `^[a-z0-9][a-z0-9._-]{1,31}$` (2–32 chars: es lo que caben `user_library.store`, `game_reviews.platform` y `game_external_ids.namespace`, todos `VARCHAR(32)`) — **abierto** |
| Etiquetas visibles | Orden del selector en `PLATFORM_CATALOG_KEYS`, etiquetas en `STORE_LABELS` de `stores.ts` («Nintendo Switch», «PSP»…), ambas consumidas por `storeLabel()`. Lo no catalogado se pinta con su propio texto: `storeLabel()` ya hace ese fallback |

El validador vive en **un solo sitio**: `StoreKeys.Normalize(value)` → alias de tienda conocida → canónica;
si no, slug válido → minúsculas; si no → 400. Lo usan el alta manual y `ReviewService.ValidatePlatform`, así
cliente y servidor aceptan exactamente lo mismo. `normalizeStore()` en `stores.ts` es el espejo.

**Por qué se cambia el plan anterior** (que proponía una lista cerrada añadida a `STORE_KEYS`): una lista
cerrada convierte cada consola nueva en código + build, y una consola olvidada en un **400 al guardar
reseña**. El catálogo sigue existiendo, pero solo decide el `<select>` y el nombre bonito.

### Medido hoy: qué rompe hoy `store='switch'` y qué hay que tocar

Las filas de consola **ya se renderizan** en `/library` (`library-contract.ts` `toStore` acepta texto
libre, línea 272); el union cerrado `StoreKey` está hilvado en los caminos de **reseña** y **posesión**:

| Punto | Síntoma actual con una consola nueva | Cambio |
|---|---|---|
| `ReviewService.ValidatePlatform` (exige `StoreKeys.IsKnown`) | **400** al guardar reseña | validar con `StoreKeys.Normalize` |
| `review-drawer.tsx` (`toStoreKey(...) === null → continue`) | la plataforma **no aparece** en el selector de reseñas | llave = string normalizado, no `StoreKey` |
| `reviews.ts` (2 parsers con `toStoreKey`) | la reseña se descarta en el cliente | parser = `normalizeStore` (acepta slug) |
| `library-contract.ts:359` (`matchedReview` exige `toStoreKey`) | la reseña existente **no se pinta** en su fila | comparar strings normalizados |
| `library-client.tsx:457` (`toStoreKey(item.store) === platform`) | tras guardar, la reseña no aparece en la fila | idem |
| `steam.ts` `toOwnershipStores` (`toStoreKey` → skip) | el badge «Ya lo tienes en …» queda **invisible** en la ficha PC | `toStoreKey(x) ?? normalizeSlug(x)`: patrón que `games-merge.ts:97` ya usa (`toStoreKey(text) ?? text`) |

Sin cambios: `LibraryStates`, `LibraryController.SourceRules` (el alta manual no pasa por el import) y
cualquier DDL.

## 4. Identidad: el id es el `game_id`

El usuario propuso «id inventado si no se tiene uno en DB». Mejor: **id asignado por la propia BD**, el
`game_id` canónico, usado dos veces —como `user_library.store_game_id` y como
`game_external_ids.external_id` del namespace de la plataforma—.

```text
user_library:  (user_id, store='switch', store_game_id='42', state='owned')   ← 42 = games.game_id
game_external_ids: (namespace='switch', external_id='42') → game_id 42
```

### Flujo del alta (una transacción, mismo advisory lock que el import)

1. **Candidatos primero**: buscar `games` por `normalized_title` (`GameTitleNormalizer.Normalize`, ya
   existe; `idx_games_normalized_title` está indexado). Si hay ≥1, el diálogo **exige elegir**: «añadir a
   este juego» (misma fila canónica, otra plataforma) o «crear nuevo» con aviso. La identidad **nunca se
   afirma por título en silencio** (`PLAN_CATALOG.md` §4); el título solo produce un candidato que una
   persona confirma.
2. **Crear** (si no hay candidato elegido): insertar la fila `games`. El `GameIdentityResolver` **no sirve
   tal cual**: exige `>= 1` external id y lanza sin ellos, y el id aún no existe. Un insert directo de
   `Game` + `GameIdentityClaimer.TryClaimAsync(platform, game.GameId)` (ya existe, `internal` en el mismo
   ensamblado) deja el mapping reclamado de forma idempotente.
3. **Fila de biblioteca**: `state='owned'`, `is_installed` opcional (por defecto `NULL` = desconocido: un
   cartucho no está «instalado»), `added_at` = ahora salvo que el usuario indique fecha.
4. **Reporte** (obligatorio, igual que el import): `created`, `attached`, `duplicateRow`,
   `candidates`.

Repetir el mismo alta en la misma plataforma lo bloquea `uq_user_library (user_id, store, store_game_id,
state)` → el reporte dice `duplicateRow`, nunca un 500.

### La misma consola, dos plataformas, y el mismo juego en PC

| Caso | Resultado |
|---|---|
| Mismo título en `switch` y `nds` | Una sola fila `games`, dos mappings (`switch`/`nds` con el mismo id), dos filas de biblioteca → `groupLibraryItems` las agrupa en **una fila con dos badges** y cada plataforma con su reseña |
| Mismo juego en Steam y en Switch | Dos filas de biblioteca con el mismo `game_id`: el badge de posesión de la ficha PC («Ya lo tienes en Nintendo Switch») sale solo, porque `GameOwnershipService` agrupa por `game_id` |
| Variante de título («Zelda BotW» vs el título real) | Crea una segunda fila `games` (candidato no coincidió) → se arregla con la **fusión manual existente** (`GameMergeService` + UI de duplicados), que ya repunta mappings, reseñas y favoritos |

### Alternativas rechazadas

| Alternativa | Por qué no |
|---|---|
| Slug del título como `external_id` | Identidad derivada de un título, prohibida por `PLAN_CATALOG.md` §1/§4: el id quedaría escrito para siempre y afirmaría identidad que el título no da |
| GUID aleatorio | No deduplica nada: dar de alta dos veces el mismo juego crearía dos filas `games` |
| No escribir `game_external_ids` | Es justamente lo pedido («meter en relación con tiendas la plataforma») y cuesta una llamada a `TryClaimAsync`: sin mapping, la plataforma no existe como namespace y una fusión futura no tendría qué mover |

**Nota tras una fusión**: `user_library.store_game_id` conserva el id histórico (p. ej. `42` cuando el
juego sobreviviente es el `57`) y el mapping `(switch, 42) → 57` sigue siendo la verdad. No se reescribe
`store_game_id` en ningún sitio hoy; si alguna vez molesta, es un `UPDATE` acotado, no una migración.

## 5. Endpoint y UI

### `POST /api/library/manual` — `AdminWithId` (misma política que el import)

```jsonc
{ "store": "switch", "title": "The Legend of Zelda: Breath of the Wild",
  "gameId": 42,            // opcional: elegir candidato en vez de crear
  "create": true,          // opcional: forzar juego nuevo aunque el título ya exista
  "igdbId": 967140,        // opcional: fila elegida en la búsqueda; la portada la tra el servidor
  "isInstalled": null,     // opcional, desconocido por defecto
  "addedAt": "2019-06-01"  // opcional
}
```

Validación: `store` vía `StoreKeys.Normalize` (**cualquier consola slug, sin catálogo cerrado**; una
tienda PC conocida también se acepta por si se quiere anotar una compra de Steam a mano), `title` no
vacío **≤ 256** (medido: `user_library.title` es `VARCHAR(256)`, el límite real de la fila, aunque
`games.title` admite 512), `gameId` opcional y existente. Responde el reporte de §4.4 (con `candidates`
cuando el título ya existe y no vienen `gameId` ni `create`: **los candidatos viajan dentro de esta
respuesta**, sin GET aparte — se descartó como código muerto, solo esta llamada puede ser negada).
**El cliente nunca manda una URL de portada** (misma regla que `covers/sync`): el diálogo manda el
`igdbId` elegido y la portada la escribe el servidor al crear, con la petición HTTP resuelta **antes** de
abrir la transacción (nada externo bajo el advisory lock).

### `GET /api/library/manual/enrich?title=` — búsqueda del diálogo (implementado así; §5 original
hablaba de `GET /api/catalog/search`)

- Responde `{ source, hits: [{ igdbId, title, releaseYear, imageUrl, platforms }], ... }` (máx. 5,
  relevancia del buscador del proveedor). `source: "igdb"` con la sonda §2.3 aprobada; `source: null` =
  **fuera de servicio** (sin credenciales o proveedor caído): el diálogo lo distingue de «sin
  resultados» y el alta sigue funcionando a mano con `image_url NULL`.
- Todo server-side: el navegador nunca habla con el proveedor. Wikidata queda como respaldo documentado
  en §2.3, no implementado: IGDB pasó la puerta, no hace falta una segunda fuente.
- Dos trampas medidas (§2.3) ya gestionadas: las URLs de cover llegan `//images.igdb.com/...` y el
  servidor las absoluta con `https:`; una búsqueda devuelve una fila por plataforma y el diálogo solo
  **muestra** — el `igdbId` elegido se vuelve a leer en el servidor al crear.
- Si el usuario escribe el título a mano y no elige ningún resultado: alta igual, `image_url NULL`.

### `DELETE /api/library/{userLibraryId}` — `AdminWithId` (deshacer del alta manual)

Medido hoy: **no existe ninguna forma de borrar una fila de `user_library`** (los únicos `HttpDelete` del
API son favoritos, reseñas, sesiones y usuarios). El alta manual abre una puerta nueva —equivocarse de
juego o de plataforma— y sin DELETE el error queda para siempre.

Borrado por id, solo filas del usuario autenticado. **No toca `games` ni `game_external_ids`** (la
identidad canónica es compartida) **ni reseñas ni favoritos** (son contenido del usuario, misma doctrina
que «el import no pruna»): el juego puede quedar sin filas de biblioteca y eso es inofensivo.

### UI `/library`

Botón **«Añadir manual»** en la cabecera de la sección (visible también con biblioteca vacía) → diálogo:
selector de plataforma (`PLATFORM_CATALOG_KEYS` + **«Otra plataforma…»** de texto libre validado con
`normalizeStore`, el espejo de `StoreKeys.Normalize`) → **«Buscar portadas»** (`GET /manual/enrich` con
los resultados elegibles: portada + año + plataformas) → «Añadir a la biblioteca» → si el servidor
responde `candidates`, lista «¿es este?» con **«Añadir aquí»** (por `gameId`) o **«Crear juego nuevo»**
(`create: true`) → confirmación con **«Deshacer»** (borra la fila vía `DELETE`) y recarga de la grilla.
`isInstalled`/`addedAt` existen en la API pero el diálogo no los expone (dejan los defaults); añadirlos
solo si hacen falta. No se toca `nav-config.ts` ni `middleware.ts`: `/library` ya existe y es privada.
En la misma cabecera vive **«Importar consolas»** (Fase 6, §7): otro botón, misma política de admin
(`LibraryController` entero es `AdminWithId`; el BFF responde 403 a quien no lo sea).

## 6. Qué no cambia

| Área | Por qué queda igual |
|---|---|
| Precios y alertas | Medida 7: ITAD no tiene tiendas de consola. El binding de la fila cae al paso final → **«Sin precios vinculados»**, o «Precio vinculado por título» si el título coincide con una única fila Steam (etiqueta ya existente y honesta: es lectura, no escribe ids). Wishlist/alertas: allowlist positiva `wished`, una fila `owned` no entra |
| `LibraryStorePriceService` | Filtra `Store == StoreKeys.Xbox`: ignora las filas de consola por completo |
| Import de Playnite (tiendas) | **Invariante: nunca elimina, desactiva ni reescribe filas manuales** (§6.1). Sin cambios: no acepta `Source: null` |
| Import de consola (Fase 6, §7) | Camino **nuevo y paralelo**: exige `Source: null`, escribe solo decisiones explícitas y es insert-only. No toca las filas de tienda ni las manuales (§7.4) |
| `covers/sync` | Ya reporta `missingWithoutSteamId` para filas sin appid; las consolas caen en ese bucket, sin error |
| Reseñas y favoritos | Funcionan sobre `game_id` + vocabulario: solo necesitan §3 |
| `SQL/schema.sql` | Ninguna línea nueva |

### 6.1 Invariante: un reimport de Playnite no toca las altas manuales

Garantía **estructural** del código actual, no una promesa que dependa de acordarse de ella. Tres razones,
todas presentes en `LibraryController.Import`:

1. **No hay poda.** El import solo hace `INSERT`/`UPDATE` de las filas que trae el payload; no existe
   código que borre ni cambie `state` de filas ausentes (comentario del propio handler: *«No prune:
   entries missing from the import stay»*). Una fila manual casi nunca viene en el payload → no se toca.
2. **Su tienda no está en `SourceRules`.** Aunque un export futuro traiga filas de consola, el par
   `(PluginId, Source)` no está en la tabla congelada → se cuentan como `unsupported` y **se ignoran**:
   nunca entran al `upsert`, así que no pueden pisar `title`, `state`, `is_installed` ni `game_id` de la
   fila manual.
3. **La clave única no colisiona.** El `upsert` busca por `(store, store_game_id, state)`; una manual es
   `(switch, '<game_id>', 'owned')` y las del export traen otra `store`. No existe camino para que un
   `updated` del import apunte a una fila manual.

Corolario: el caso peligroso de §8 (payload con `Source: null` → 400 en toda la petición) tampoco amenaza:
**no corre ni un `UPDATE`**, así que las manuales quedan intactas. El riesgo real es una poda **futura**:
si algún día se añade, debe excluir por `store` fuera de `SourceRules`, no por ausencia en el payload.

La importación de consola (§7) obedece la misma invariante por construcción, y de forma aún más estricta:
solo escribe las decisiones que el payload trae (insert-only), no tiene poda y **nunca** toca una fila de
tienda ni una manual que no haya sido decidida explícitamente. Un commit repetido reporta `already_present`
y no escribe nada.

## 7. Fase 6: carga masiva desde el export de Playnite (implementada)

El alta manual de §5 sirve para unas pocas filas; la colección de consola entera se importa desde el
mismo export de Playnite. El script de `PLAYNITE_EXPORT.md` §2.3 ya escribe `Platforms` (nombres) y
**conserva `Source: null`** en las filas que no son de tienda, y ese `null` es lo que separa las dos
importaciones.

### 7.1 Dos puertas, ninguna se traga a la otra

| Origen de la fila | Endpoint | `Source` | BFF |
|---|---|---|---|
| Tienda (Steam, Epic, Xbox…) | `POST /api/library/import` (congelado) | **no nulo** (`RequireText`) | `app/api/bff/library/import` |
| Consola / manual | `POST /api/library/console-import/preview` y `…/commit` | **null explícito** | `app/api/bff/library/console-import/{preview,commit}` |

Ambas son `AdminWithId`. Un nombre de tienda en la puerta de consola es **400**; un `Source` nulo en la
puerta de tiendas es **400** de todo el payload. La frontera es simétrica y ninguna de las dos adivina.

### 7.2 Flujo: archivo → preview → decisiones explícitas → commit

1. **Archivo** (`console-import-dialog.tsx`, paso «Archivo»): se lee el JSON en el navegador y
   `parseConsoleImportFile` (`lib/contracts/console-import.ts`) separa lo importable de lo que no —
   filas de tienda, sin id, sin título, título > 256, id repetido, fila ilegible— con su motivo a la
   vista (`console-import-file-summary.tsx`). **Nada de eso se envía.** Topes: 10 MiB, raíz de arreglo y
   **2000 filas importables**: si el archivo contiene más, ninguna se oculta ni se envía y el diálogo exige
   dividirlo antes del preview. Una fecha de alta ilegible se descarta (la fila entra sin fecha) en vez de
   reventar el payload.
2. **Preview** (`console-import/preview`): **solo lectura**. Por entrada devuelve los candidatos del
   catálogo por `normalized_title` (máx. 10, con su `inLibrary`) y las **sugerencias** de plataforma que
   `ConsolePlatformCatalog` infiere del nombre de Playnite, más `needsPlatform` cuando no hay ninguna.
   No escribe, no afirma identidad y no afirma posesión.
3. **Decisiones** (paso «Revisar», `console-import-review.tsx`): cada fila resuelve a mano dos cosas —
   la plataforma que de verdad se tiene (`ownedPlatform`) y la identidad (`attachGameId` de un juego del
   catálogo, o `create: true` para uno nuevo, que es el default del borrador y la única acción que no
   afirma nada). Filtros «sin plataforma» / «con coincidencias», asignación de plataforma en bloque a las
   pendientes, paginado de 25.
4. **Commit** (`console-import/commit`): valida el payload entero antes de la primera escritura. Un
   **conflicto de identidad** rechaza **todo** el commit con **409** y `applied: false` (nada escrito);
   si aplica, responde `created | attached | already_present` por entrada.

Límite único: **2000** filas importables por archivo y por petición (`ConsoleImportLimits.MaxEntries`).
El commit es atómico, así que el diálogo bloquea el preview si se excede: no trocea ni escribe una parte.
Título ≤ **256** (el ancho real de `user_library.title`, no el de `games.title`).

### 7.3 Identidad: las mismas reglas del alta manual

- El id es siempre el `game_id` canónico, escrito como `store_game_id` y reclamado en
  `game_external_ids` con `GameIdentityClaimer.TryClaimAsync` (idempotente).
- `create: true` inserta la fila `games` (solo `Title` + `NormalizedTitle`); `attachGameId` exige que el
  juego exista (si no, **400**, no un conflicto) y comparte su ficha y sus badges de posesión.
- El commit corre bajo el mismo advisory lock `dealext.game_identity` que el import de tiendas, el alta
  manual y la fusión: la identidad canónica se serializa.
- **Conflicto** = el mapeo `(plataforma, game_id)` ya pertenece a otro juego canónico (típico tras una
  fusión). Se rechaza el commit completo; en la fila se elige otro candidato o se crea un juego nuevo.

### 7.4 Invariantes del camino masivo

| Invariante | Cómo se cumple |
|---|---|
| **`Platforms` no es posesión** | Es metadato: solo produce sugerencias. La plataforma que se escribe es la que la persona eligió; un nombre que el catálogo no mapea **no** produce slug, produce `needsPlatform` — degrada a preguntar, nunca a un slug equivocado |
| **El título no es identidad** | El título solo produce *candidatos*; `create` es explícito y el borrador arranca en «crear juego nuevo»: nunca se adjunta el primer candidato en silencio |
| **Insert-only** | Repetir el commit escribe 0 filas (`already_present`). No hay `UPDATE`, ni `DELETE`, ni poda |
| **No toca PC ni las manuales** | Exige `Source: null` y rechaza las 8 tiendas de `StoreKeys` como `ownedPlatform`; solo escribe filas de las decisiones enviadas |
| **Sin enriquecimiento externo** | Este camino **no** llama a IGDB: los juegos nuevos entran con `image_url NULL` y sin año. La portada se añade después con el alta manual (§5) o `covers/sync` |
| **Sin DDL** | Nada nuevo en `SQL/schema.sql`; los valores caben en `store`/`namespace` `VARCHAR(32)` (§3) |

### 7.5 Qué comparte con el §5

El modelo de candidato (`ManualGameCandidate`), `GameTitleNormalizer`, `GameIdentityClaimer`, el parser
de fechas `PlayniteDates` (`/Date(<epoch>)/` o ISO), `StoreKeys.Normalize`, `LibraryStates.Owned` y la
clave única `uq_user_library`. La única pieza nueva es el catálogo de sugerencias
(`ConsolePlatformCatalog`, tabla de nombres visibles → slug, con las plataformas PC ausentes a propósito)
y el límite atómico de 2000 filas por archivo.

Superficie nueva: `Deals.BusinessLogic/Models/Library/ConsoleImport.cs`,
`Deals.BusinessLogic/Services/ConsoleLibraryImportService.cs`, `ConsoleImportModels.cs` en la API,
`console-import.ts` + diálogo y componentes en `Deals.Web`, y las dos rutas BFF.

### 7.6 Estado y qué falta

**Implementado; verificación pendiente** (build/tests y corrida en vivo las valida quien publique, no
esta nota). Evidencia a recoger: `dotnet build`, `pnpm build`, los **26** tests de contrato de
`console-import.test.ts` y una corrida real. El export medido (`PLAYNITE_EXPORT.md` §4, 2026-09-18) **no
trae `Platforms` ni filas con `Source: null`**, así que la puerta de consola todavía no se ha ejercitado
con datos reales: hay que reexportar con el script actualizado y comprobar la lista de §3 de ese
documento antes de dar la fase por verificada.

## 8. Riesgos

| Riesgo | Mitigación |
|---|---|
| Duplicados por variante de título | Candidatos obligatorios en el diálogo (§4.1) + fusión manual existente. Un duplicado es visible y arreglable; nunca cuesta un precio ni una reseña equivocada |
| **Un export de Playnite que incluya consolas revienta el import de tiendas**: `PluginId`/`Source` son `RequireText` y un `null` lanza → **400 en todo el payload**, no solo en esa fila | **Resuelto por diseño (Fase 6)**: esas filas tienen su propia puerta (`/console-import/preview|commit`), que exige `Source: null`, y el diálogo descarta las filas de tienda antes de enviar. El import de tiendas sigue congelado y con la exigencia de `Source` no nulo, así que ninguno de los dos acepta el payload del otro. Medida 8: el export real no tiene filas con `Source` nulo |
| Reexportar el archivo entero por el camino equivocado | El parser de consola marca las filas de tienda como `store_row` y no las envía; al revés, un `Source` de consola en la importación de tiendas es 400 visible, no una escritura silenciosa |
| El camino masivo no trae portada ni año (no llama a IGDB) | Aceptado a propósito: `image_url NULL` es un estado soportado; la portada se añade con el alta manual (§5) o `covers/sync`. Una portada no decide identidad ni precio |
| IGDB cae o expira el token (`client_credentials` caduca ~60 días) | Sonda §2.3 aprobada (2026-09-21): si cae, respaldo Wikidata sin portada y el alta manual nunca se bloquea. Renovar el token es repetir el intercambio, sin tocar código |
| Una plataforma mal escrita («swtich») fragmenta el dato | El `<select>` del catálogo es la vía principal y «Otra…» solo para consolas no listadas; `DELETE` (§5) deshace la fila equivocada; una consola desconocida ya **no** produce 400 (§3) |
| `store_game_id` ≠ `game_id` tras una fusión | El mapping es la verdad (§4); documentado, sin reescritura |
| Portada ausente en filas sin fuente | `image_url NULL` ya es un estado soportado (filas del import actual lo tienen) |

## 9. Verificación

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
cd Deals.Web && node --test lib/contracts/*.test.ts
```

| Paso | Evidencia esperada |
|---|---|
| Vocabulario abierto (§3) | Control previo: con `store='switch'` en la BD, guardar reseña devuelve **400**; tras §3, una consola **nunca vista** (`snes`) hace alta + reseña **200** + badge de posesión visible en la ficha PC, **sin añadir ninguna constante** |
| `DELETE /api/library/{id}` | Borra solo esa fila; `games`, `game_external_ids`, reseñas y favoritos intactos; repetir el borrado → 404 |
| Alta nueva | 1 fila `games` + 1 mapping `(switch, <game_id>)` + 1 fila `user_library`, reporte `created: 1` |
| Repetir el mismo alta | `duplicateRow: 1`, **0** filas nuevas en las tres tablas |
| Mismo título en dos plataformas | Una sola `games`, dos mappings, dos filas de biblioteca → **una** fila en la UI con dos badges y reseña por plataforma |
| Candidato existente | Con `gameId` de un juego ya en la biblioteca (p. ej. de Steam): no se crea `games`, la fila de consola comparte `game_id` y el badge de posesión aparece en la ficha PC |
| Reimport del export (§6.1) | Las filas manuales siguen presentes **y sin cambios**: mismo `state='owned'`, mismo `game_id`, mismo `added_at`; `imported`/`updated` solo cuentan filas del payload |
| Invariante §6.1 | Snapshot antes/después: `SELECT user_library_id, store, state, game_id, added_at FROM user_library WHERE store NOT IN ('steam','epic','gog','xbox','amazon','ubisoft','humble','battlenet')` → **idéntico** en las dos corridas; ninguna fila cambia de `state` |
| Binding | La fila de consola muestra «Sin precios vinculados» o «Precio vinculado por título», **nunca** un precio inventado |
| UI | Filtro por plataforma en `/library` con etiqueta visible y contador correcto; búsqueda del diálogo devuelve candidatos con control negativo (título inexistente → lista vacía, no error) |
| Consola: preview | `POST /api/library/console-import/preview` con N filas `Source: null` → **0 escrituras**; responde N entradas en el mismo orden con candidatos y sugerencias, y `needsPlatform: true` cuando el nombre de Playnite no está en el catálogo |
| Consola: frontera de `Source` | El mismo payload con `Source: "Steam"` → **400**; el payload de tiendas en `/api/library/import` con alguna fila de `Source` nulo → **400** de todo el archivo (sigue congelado) |
| Consola: commit | Reporte por entrada `created`/`attached`/`already_present`; repetir el mismo commit → **todas** `already_present` y **0** filas nuevas en `games`, `game_external_ids` y `user_library` |
| Consola: conflicto | `attachGameId` cuyo mapeo `(plataforma, game_id)` pertenece a otro juego tras una fusión → **409** con `applied: false`, `conflicts` no vacío y **0** escrituras |
| Consola: invariante PC/manual | Snapshot de filas de tienda y manuales antes/después de un commit de consola → **idéntico**; el commit solo escribe sus propias decisiones |
| Consola: sin enriquecimiento | Juego creado por el commit → `image_url IS NULL` y `release_year IS NULL`; la portada solo aparece si se añade después con §5 o `covers/sync` |
| Consola: límites | Payload de 2001 entradas → **400**; título de 257 → **400** (no un 500 de Postgres por `VARCHAR(256)`) |
| Consola: UI | «Importar consolas» con el export completo: las filas de tienda se listan como descartadas y **no se envían**; se importan solo las resueltas y la grilla se recarga |

> **Estado**: todas las filas de Fase 6 están **pendientes de verificación en vivo**. El export medido no
> trae filas de consola ni `Platforms` (§7.6), así que ninguna corrida con datos reales ha ejercitado estas
> puertas todavía; el build y los tests de contrato son la validación mínima de esta entrega.

Consultas de comprobación (solo lectura):

```sql
SELECT store, count(*) FROM user_library GROUP BY store ORDER BY 2 DESC;
SELECT namespace, count(*) FROM game_external_ids WHERE namespace NOT IN
  ('steam','itad','epic','xbox') GROUP BY namespace;
-- Filas de consola sin identidad canónica: debe ser 0 tras un alta correcta
SELECT count(*) FROM user_library ul
  WHERE ul.store NOT IN ('steam','epic','gog','xbox','amazon','ubisoft','humble','battlenet')
    AND ul.game_id IS NULL;
```
