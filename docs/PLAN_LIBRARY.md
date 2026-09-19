# DealExt: plan de biblioteca de juegos comprados

Estado: propuesto, pendiente de aprobación. Solo análisis y plan.

Alcance de esta fase: subir el export de Playnite, crear la biblioteca, **intentar el binding con
precios** y dejar una nota explícita de "sin binding" cuando no se pueda. Después, las reseñas por
plataforma (§9). Sin integraciones por tienda y sin Steam por API: eso queda diferido (§10).

Alcance del producto: dos partes. **Wishlist y precios** (`PLAN_WISHLIST.md`) y **biblioteca con reseñas**
(este plan, que en esta fase construye la biblioteca y el binding).

Continúa a `PLAN_WISHLIST.md`, `PLAN_CATALOG.md` y `PLAN_IMPLEMENTACION_BIBLIOTECA.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

Este plan absorbe la mitad "biblioteca poseída" que `PLAN_WISHLIST.md` declaraba en sus §3, §4 y §5.

## 1. La decisión: de dónde sale la biblioteca

| Pregunta | Respuesta |
|---|---|
| ¿Se puede traer de cada tienda por su API? | **No de forma uniforme.** Solo Steam tiene API oficial de biblioteca propia (`IPlayerService/GetOwnedGames`). Epic, GOG, Ubisoft, Xbox, Amazon y Battle.net no la tienen: son API no documentada, protocolo reversado o cookie de sesión. |
| ¿Mejor subir el export de Playnite? | **Sí, y es lo único que hace falta en esta fase.** Playnite ya agrega Steam, Epic, GOG, Amazon, Battle.net, Ubisoft y Xbox con sus plugins de biblioteca, y el usuario ya lo tiene instalado. Una sola subida cubre todas las tiendas sin que DealExt guarde credenciales de ninguna. |
| ¿Ya existe herramienta que lo junte todo? | **Sí: Playnite**, que ya está instalado. **Backlogia** (`sam1am/backlogia`) es la referencia de cómo hacerlo todo server-side, por si algún día hay que prescindir de la máquina Windows. |
| ¿Hace falta escribir el script de export? | **Sí, para JSON.** El export real confirma que el script propio entrega los campos correctos. Los exporters JSON de terceros omiten `GameId`/`PluginId`; Library Exporter Advanced sirve como fallback CSV (§6). |
| ¿Hace falta la API de Steam entonces? | **No en esta fase.** El plugin de Steam de Playnite ya exporta los juegos de Steam. La API oficial solo añadiría frescura sin depender de la máquina Windows → §10. |

Decisión: **Playnite es la única fuente de esta fase.** Cero integraciones por tienda, cero OAuth, cero
scraping.

## 2. Tratamiento por tienda

| Tienda | Entra en la biblioteca | Entra en precios | Cómo se resuelve su identidad |
|---|---|---|---|
| Steam | ✅ | ✅ | `store_game_id` **es** el appid. Binding directo |
| Epic | ✅ | ✅ si liga | Mapeo `('epic', id)` + candidato de precio por título; ITAD como mejora diferida |
| GOG | ✅ | ✅ si liga | Ídem |
| Ubisoft Connect | ✅ | ✅ si liga | Ídem |
| Humble | ✅ | ✅ si liga | Mapeo `('humble', id)` + candidato de precio por título |
| Battle.net | ✅ | ✅ si liga | Mapeo `('battlenet', id)` + candidato de precio por título |
| **Amazon / Prime Gaming** | ✅ | ✅ si liga | Por título contra Steam, nunca por ITAD; el export muestra que no todas tienen gemela Steam (§3.1) |
| **Xbox** | ✅ | **❌ nunca en V1** | Las 348 filas se importan como `subscription`: el export no distingue compra de Game Pass (§5) |

### Amazon: no se busca en ITAD; binding candidato contra Steam

ITAD **no tiene** tienda de Amazon (son 34 tiendas verificadas): buscar ahí sería trabajo perdido. El
export real corrigió además el supuesto inicial: contiene **195** filas de Amazon y solo **33** tienen
coincidencia de título con Steam, aun eliminando sufijos como `GOTY`, `Definitive`, `Complete`, `Enhanced`,
`Remastered`, `Deluxe` y `Edition`.

Regla V1:

- Amazon siempre se importa como `store='amazon'` y con su `GameId` propio.
- Si su título normalizado tiene **una única** entrada Steam candidata, la UI puede mostrar su precio como
  **"Precio vinculado por título"**.
- **No se fusionan** sus filas `games` automáticamente. Una coincidencia de título no es identidad y una
  fusión errónea contaminaría reseñas, precios y badges.
- Las otras `162` filas actuales quedan con **"Sin precios vinculados"**. Es información correcta, no un
  fallo de import.

### Game Pass: biblioteca sí, precios no

El export solo expone `Source='Xbox'`; no distingue compra de catálogo Game Pass. Decisión confirmada:
las **348** filas Xbox del primer import se guardan como `state='subscription'`, tag `Game Pass`, sin
precio, alerta ni badge de propiedad. Ver §5.

## 3. Evidencia verificada en vivo

Reproducible sin credenciales:

```bash
# 34 tiendas, con nombre e id
curl -s "https://api.isthereanydeal.com/service/shops/v1?country=MX"
```

| Prueba | Resultado |
|---|---|
| Las 34 tiendas de ITAD | **No hay ninguna de Amazon.** Xbox solo existe como `Microsoft Store` (48) |
| `POST /lookup/id/shop/61/v1 ["app/1091500"]` | UUID de Cyberpunk 2077 (Steam) |
| `POST /lookup/id/shop/35/v1 ["1423049311"]` | El **mismo** UUID (GOG) → identidad cross-store |
| `POST /lookup/id/shop/4/v1 ["1"]` (Blizzard, fuera del allowlist) | `HTTP 200 {"1":null}` → la tienda se acepta |
| `POST /lookup/id/shop/999999/v1 ["1"]` (control) | `HTTP 400` → el test sí discrimina |
| `POST /lookup/id/shop/abc/v1 ["1"]` (control) | `HTTP 404` |
| `POST /lookup/id/shop/52/v1`, `/37/v1`, `/48/v1`, `/16/v1`, `/62/v1` | `HTTP 200` en todas |

Sin los dos casos de control, el resultado de Blizzard/EA/Humble no probaría nada: un error genérico se
habría confundido con aceptación.

También aclara una confusión fácil: `ItadOptions.OfficialShopIds` (`61,35,16,62,48,6,36,20,50,64`) filtra
**ofertas** de `prices/v3`. El endpoint de identidad acepta además 4, 52 y 37. Son cosas distintas.

## 4. Lo que ya existe y no hay que crear

Corrige lo que `PLAN_WISHLIST.md` §2 afirmaba: esto **ya está implementado** en el checkout.

| Ya implementado | Cómo se usa aquí |
|---|---|
| Tabla `user_library` + entidad `Deals.Models/Entities/UserLibrary.cs` + `DbSet` en `AppDbContext` | Destino del import |
| `uq_user_library UNIQUE (user_id, store, store_game_id, state)` | Reimportar el mismo JSON es upsert, no duplica |
| `idx_user_library_user_itad`, `idx_user_library_user_title` | Consulta de binding y fusión por título, ya indexadas |
| `steam_games` (`app_id`, `name`, `itad_game_id`) | Destino del binding. `name` es la llave del match por título |
| `game_offers` + `selectBestPrice` | Precio que se muestra en una entrada ligada |
| `ISteamGameService` | Insertar en `steam_games` los juegos que el usuario busque, para que el binding mejore solo |
| `Deals.Web/lib/contracts/steam.ts` (normalizadores estrictos) | Extender con los tipos de biblioteca, sin relajar validación |
| `WishlistSyncService` | Escribe `state='wished'` y **nunca toca** filas `owned`. Aquí se añade la otra mitad |

Falta por construir: el endpoint de import, la página `/library`, el badge, la lógica de binding y las
reseñas.

**DDL de esta parte: ninguno.** El único esquema nuevo del conjunto es el del catálogo canónico
(`PLAN_CATALOG.md` §2), que además es quien da a `user_library` su columna `game_id`.

## 5. Fase 0: Game Pass como suscripción

Game Pass se guarda con **`state = 'subscription'`** y `store = 'xbox'`. No es posesión: la UI muestra un
tag **"Game Pass"** y **nunca** un badge de "ya lo tienes".

### Por qué un estado y no una columna

Se evaluaron tres formas y solo una funciona:

| Opción | Veredicto |
|---|---|
| Columna `is_subscription BOOLEAN` | **Rompe la coexistencia.** Un juego de Xbox que tienes *y* que además está en Game Pass comparte `(user_id, store='xbox', store_game_id)` y colisiona en `uq_user_library` |
| Tabla aparte | Obliga a un segundo camino de import y convierte cada lectura de biblioteca en un `JOIN`/`UNION` |
| **`state = 'subscription'`** | `state` es `VARCHAR(16)` de texto libre, sin `CHECK`, y **ya está dentro de la clave única**: un tercer valor no cuesta nada y permite que las filas coexistan |

**Coste en código: cero.** En todo el repo no existe ninguna constante `owned`; solo `wished` está
hardcodeado (`WishlistSyncService.cs:29`, `WishlistController.cs:19`), y toda consulta filtra por
`state == 'wished'`. Un valor nuevo lo ignora todo el código actual.

### Reglas

- **Nunca entra en precios.** Aunque la entrada acabe ligada, la UI de biblioteca suprime el precio cuando
  `state = 'subscription'`. Es suscripción: puede terminar.
- **Nunca genera alertas.** La regla de `PLAN_WISHLIST.md` §7 es una **allowlist positiva `state='wished'`**,
  no una lista negra de `owned`. Con lista negra, `subscription` se colaría.
- **Nunca cuenta como propiedad** en ningún cálculo de ahorro futuro.
- El plugin Xbox **sí** emite `GameId`: los 348 valores reales son package/console ids no numéricos. Se
  guardan como `('xbox', GameId)` para identidad de biblioteca, pero siguen excluidos de precios.

## 6. Fase 1: subir el export de Playnite y crear la biblioteca

### El export

| Opción | Formato | Cuándo usarla |
|---|---|---|
| Script PowerShell propio (`PLAYNITE_EXPORT.md`) | JSON | **Usado y verificado**: incluye todos los campos que necesita DealExt |
| `darklinkpower/PlayniteExtensionsCollection` → Library Exporter Advanced | CSV configurable | Fallback si Playnite no carga el script |
| `NicodeSS/playnite-game-data-exporter` | JSON | **No usar**: omite `GameId` y `PluginId` |

El *Library Exporter* integrado de Playnite (`LibraryExporterPS_Builtin`) queda descartado: emite CSV con
`Name, Source, ReleaseDate, Playtime, IsInstalled` y **sin el id de tienda**, que es justo lo que hace
falta para ligar precios.

### El contrato

| Campo Playnite | Campo `user_library` | Nota |
|---|---|---|
| `GameId` | `store_game_id` | Para Steam es el appid; para el resto es id propio de proveedor |
| `PluginId` + `Source` | `store` | Mapa verificado en la tabla de contrato real |
| `Name` | `title` | Solo candidato de binding por título; nunca identidad |
| `IsInstalled` | `is_installed` | Distingue instalado de solo-en-biblioteca |
| `Added` | `added_at` | Alta en la biblioteca, no fecha de compra |

### Contrato real verificado: `docs/dealext-library.json`

El JSON real tiene raíz de arreglo y **2586** filas; todos los campos críticos están presentes.

| `Source` | `PluginId` | Filas | `GameId` |
|---|---|---:|---|
| `Steam` | `cb91dfc9-b977-43bf-8e70-55f46e410fab` | 1168 | appid numérico |
| `Epic` | `00000002-dbd1-46c6-b5d0-b1ba559d10e4` | 394 | identificador no numérico |
| `GOG` | `aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e` | 342 | product id numérico |
| `Xbox` | `7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287` | 348 | package/console id no numérico |
| `Amazon` | `402674cd-4af6-4886-b6ec-0e695bfa0688` | 195 | `amzn1.adg.product.*` u otro id no numérico |
| `Ubisoft Connect` | `c2f038e5-8b92-4877-91f1-da9094155fc5` | 89 | id numérico |
| `Humble` | `96e8c4bc-ec5c-4c8b-87e7-18ee5a690626` | 39 | id no numérico |
| `Battle.net` | `e3c26a3d-d695-4cb7-a769-5ff7612c7edd` | 11 | id no numérico |

`Added` **no** es ISO: Playnite serializa .NET JSON como `\/Date(<epoch-milisegundos>)\/`. El import debe
aceptar exactamente ese formato y convertirlo a UTC; no debe exigir ISO ni tratarlo como texto opaco.

`Source` es la autoridad para mapear la tienda; `PluginId` se valida contra la tabla anterior para detectar
un export de otro plugin/versión. Una combinación desconocida se rechaza como `unsupportedSource`, no se
adivina. El mapa se congela con esta fixture personal ignorada por Git.

### Endpoint

- `POST /api/library/import` — **`AdminWithId`**. Recibe el JSON, hace upsert y **descarta el archivo**.
  No se persiste el JSON crudo ni rutas del sistema de archivos del usuario.
- Validación: tamaño máximo; arreglo JSON; campos requeridos `GameId`, `PluginId`, `Source`, `Name`,
  `IsInstalled`, `Added`; `GameId`/`PluginId`/`Source`/`Name` no vacíos; `IsInstalled` booleano; y
  `Added` ISO o `\/Date(<epoch-milisegundos>)\/` válido. Campos extra se ignoran para tolerar exports
  futuros.
- El estado se decide por fuente: `Xbox` → `subscription`; todas las demás fuentes actuales → `owned`.
- Upsert idempotente por `uq_user_library`: reimportar el mismo export no duplica ni pierde nada.
- **No borra lo que no viene en el archivo.** Borrar entradas ausentes rompería las reseñas de §9, que son
  contenido del usuario. Decisión: no se pruna nada.
- Tras el upsert se llama al resolver de `PLAN_CATALOG.md` §4 para cada entrada.
- Reporte obligatorio: `imported`, `updated`, `unresolved`, `unsupportedSource`, `byStore`. Sin esto, un
  import roto pasa desapercibido.

## 7. Fase 2: binding con precios y nota de "sin binding"

Binding = **¿se puede llegar a una fila de `steam_games` (y por tanto a `game_offers`) desde esta entrada?**

| Orden | Regla | Resultado |
|---|---|---|
| 1 | `state = 'subscription'` | Tag `Game Pass`; no se intenta precio |
| 2 | `games` tiene `('steam', appid)` y existe fila `steam_games` | Precio ligado por identidad exacta |
| 3 | `games` tiene `('itad', uuid)` y existe `steam_games.itad_game_id = uuid` | Precio ligado por identidad exacta |
| 4 | El título normalizado devuelve **una** fila `steam_games` | `Precio vinculado por título`; candidato de solo lectura |
| 5 | Cero o más de una candidata | **Sin precios vinculados** |

Los pasos 2–3 son `JOIN`s por identidad exacta. El paso 4 nunca escribe ids, no fusiona filas `games`, no
agrupa tiendas y no permite un badge de propiedad. Es el binding que cubre Amazon, Epic, GOG, Humble,
Battle.net y Ubisoft hasta que exista identidad exacta multi-tienda.

### Qué ve el usuario

| Estado | Qué muestra la biblioteca |
|---|---|
| Identidad exacta | Precio actual, descuento y mínimo histórico de `game_offers` vía `selectBestPrice` |
| Candidato por título | El mismo precio, etiquetado **"Precio vinculado por título"** |
| Sin binding | Nota explícita **"Sin precios vinculados"** y el título tal cual, sin precio inventado |
| `state='subscription'` | Tag **"Game Pass"**, sin precio aunque coincida por título |

La nota no es un error: significa "este título todavía no está en el catálogo del comparador". Nunca se
escribe un precio parcial ni un cero cuando no hay binding.

### El binding mejora sin reimportar

El backfill de `PLAN_CATALOG.md` §5 es re-ejecutable para ids exactos. El candidato por título se calcula
al leer: si el usuario busca después el juego en el comparador y queda una única fila Steam, aparece en la
siguiente carga sin borrar ni reimportar nada.

## 8. Fase 3: UI de la biblioteca

- Ruta **`/library`**. Es privada por defecto: el `config.matcher` global de `Deals.Web/middleware.ts` ya
  cubre todas las rutas de app y `isPublicRoute` es una allowlist. Solo hay que añadir la entrada en
  `components/navigation/nav-config.ts`.
- Lista con filtro y contador por tienda, e indicador de cuántos títulos quedaron **sin binding**. Game
  Pass aparece con su tag, en su propio grupo o filtro.
- **Agrupación solo por identidad exacta.** Entradas que comparten el mismo `game_id` se muestran en una
  sola fila con sus tiendas y un indicador de "instalado" si alguna lo está. Una coincidencia por título
  (Amazon incluido) mantiene filas separadas y muestra su binding como candidato: V1 no auto-fusiona.
- Badge de propiedad en `Deals.Web/app/games/[steamAppId]/game-client.tsx`: **"Ya lo tienes en GOG"**
  solo con identidad exacta, **"Game Pass"** para `store='xbox'` con `state='subscription'`, y
  **"Posible coincidencia"** para un binding por título que no fusiona identidades.
- UI de import: subir el JSON y mostrar el reporte. Nunca el JSON crudo ni rutas locales.
- Actualizar `Deals.Web/DESIGN.md` como parte del cambio.

## 9. Fase 4: reseñas por plataforma

Segunda mitad de la parte biblioteca. Un juego se puede reseñar **cuantas veces se juegue**: un final en
2026 y otro en 2030 son dos reseñas y las dos se conservan. No hay clave única por
`(user_id, game_id, platform)` — la quitó `2026-09-30_game_reviews_multiple.sql` — así que la identidad de
una reseña es su `game_review_id`, y `(game_id, platform)` solo agrupa: la fila de biblioteca pinta la más
reciente y el drawer lista todas.

### Por qué las reseñas no cuelgan de `user_library`

Una fila de `user_library` es un **artefacto de import**: `state` está dentro de su clave única, así que
`wished`, `owned` y `subscription` coexisten como filas distintas para el mismo juego. Si una reseña
colgara de ahí, un reimport o un cambio de estado la rompería. `(game_id, platform)` es estable.

### Esquema

```sql
-- SQL/migrations/2026-09-28_game_reviews.sql  (+ bloque en SQL/schema.sql)
CREATE TABLE game_reviews (
    game_review_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    platform VARCHAR(32) NOT NULL,   -- mismo vocabulario que user_library.store
    started_month DATE,              -- día 1 del mes; el frontend usa <input type="month">
    finished_month DATE,
    score SMALLINT,                  -- 0..100
    is_goty BOOLEAN NOT NULL DEFAULT FALSE,
    body TEXT,                       -- opinión en texto
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

CREATE INDEX idx_game_reviews_game ON game_reviews(game_id);
CREATE INDEX idx_game_reviews_user_game_platform ON game_reviews(user_id, game_id, platform);
```

Decisiones:

- **Mes con `DATE` truncado al día 1**, no `VARCHAR(7)`: sigue siendo comparable y ordenable, y mapea
  directo a `YYYY-MM` en la UI. La duración se deriva de `started_month` y `finished_month`, no se guarda.
- **La etiqueta de la nota no se persiste**, se calcula. Cambiar los rangos reetiqueta el histórico sin
  migración:

| Rango | Etiqueta |
|---|---|
| 0–19 | malo |
| 20–39 | flojo |
| 40–59 | regular |
| 60–74 | bueno |
| 75–89 | muy bueno |
| 90–100 | obra maestra |

  `null` = sin puntuar. Detrás de un único `ReviewScoreBands.Label(short? score)` para que la UI no
  replique los umbrales. **Los rangos son ajustables**: si no te cuadran, se cambia la constante.

- **Sin `CHECK` de rango**, coherente con el repo: `schema.sql` no tiene ni un solo `CHECK`. Las
  validaciones (`0 ≤ score ≤ 100`, `finished_month ≥ started_month`, día = 1) van al servicio. Si
  prefieres verdad en la base de datos, `CHECK (score BETWEEN 0 AND 100)` es barato, pero rompe el estilo.
- **Sin UNIQUE a propósito.** Rejugar un juego es el motivo de la tabla: una partida, una reseña. Con
  `UNIQUE (user_id, game_id, platform)` la segunda reseña pisaba a la primera o se rechazaba.
  `game_review_id` es la identidad, y `platform` sigue siendo el vocabulario compartido de tienda.
- **La propiedad no se impone por FK.** Si una reseña debe exigir que poseas ese juego en esa plataforma,
  se comprueba en el servicio contra `user_library.state`. No se acopla la tabla a un artefacto de import.

### Identidad de la plataforma

`platform` usa el mismo vocabulario que `user_library.store`, así que la reseña y la entrada de biblioteca
se cruzan por texto sin traducción. Un juego sin fila `games` no puede reseñarse: el catálogo es la
dependencia dura de esta fase.

## 10. Diferido

Dos mejoras que **no** son necesarias. Se documentan para no volver a investigarlas.

### `LookupByShopAsync` — identidad exacta por tienda

Hoy el shop 61 está fijo en el path de `IItadClient`, así que solo se resuelven appids de Steam. Un método
nuevo, extensión del cliente existente y no un cliente nuevo:

```csharp
Task<IReadOnlyDictionary<string, string?>> LookupByShopAsync(
    int shopId, IReadOnlyCollection<string> shopGameIds, CancellationToken ct);
```

Daría mapeos `('itad', uuid)` **exactos** en lugar de fusión por título. Lotes de ~200 ids por llamada, vía
`ProviderRequestGovernor`. Formatos de id confirmados: Steam `app/<appid>` y GOG numérico a secas. Epic,
Ubisoft, Microsoft, Blizzard y EA **sin verificar**.

### Steam por API oficial — frescura sin la máquina Windows

| Aspecto | Detalle |
|---|---|
| Endpoint | `GET https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/` |
| Parámetros | `key`, `steamid`, `include_appinfo=1`, `include_played_free_games=1` |
| Trampa de nombre | **No existe `appids_only`**. "Solo appid" es `include_appinfo=0` |
| Key | User Web API key de `steamcommunity.com/dev/apikey`, server-side, **100.000 llamadas/día**. Nunca al cliente |
| Prerrequisito | La cuenta debe tener **"Game details" público**. Perfil público **no basta** |
| No devuelve | Fecha de compra. Y biblioteca vacía es indistinguible de perfil no público |

### Duración de juegos

HowLongToBeat e IGDB se documentan en `PLAN_CATALOG.md` §6, junto con la forma de almacenamiento y el
hallazgo de cumplimiento que impide crawlear HLTB.

## 11. Orden de fases

```
PLAN_CATALOG.md   ← base compartida; sin ella las reseñas no tienen dónde colgar
  Fase 1  games + game_external_ids + backfill
  Fase 2  resolver en los sitios de inserción

PLAN_LIBRARY.md   (objetivo de la fase actual)
  Fase 0  Game Pass como state='subscription'     ← horas, independiente, sin esquema
  Fase 1  import de Playnite → crear la biblioteca
  Fase 2  binding con precios + nota de "sin binding"
  Fase 3  UI /library + badges
  Fase 4  reseñas por plataforma
```

El catálogo va antes porque el binding de la Fase 2 y las reseñas de la Fase 4 dependen de `game_id`. La
Fase 0 no depende de nada y se puede hacer hoy.

**Antes de empezar hay que commitear ITAD y FX**, que siguen staged sin commitear: si no, las migraciones
fechadas de `PLAN_CATALOG.md` pierden el orden.

## 12. Riesgos

| Riesgo | Mitigación |
|---|---|
| El export de Playnite cambia de forma entre versiones | El import valida y reporta; no falla en silencio |
| `PluginId` (GUID) y `Source.Name` sin mapear | Reporte de no resueltos; nunca adivinar. §Anexo B |
| Fusión por título con falsos positivos (ediciones, remasters) | `PLAN_CATALOG.md` §4: **no hay auto-fusión** en V1; un falso positivo solo deja una fila sin resolver, nunca un precio equivocado |
| Un reimport borra entradas que ya no están en el export | Decisión explícita: **no se pruna nada**; hay reseñas de usuario colgando |
| Cientos de entradas resolviendo título en cada import | La resolución ocurre en el import, no en cada lectura; `normalized_title` está indexado |
| El usuario lee "sin binding" como error | Nota explícita "Sin precios vinculados"; nunca un precio parcial ni un cero |
| Game Pass tratado como compra | `state='subscription'` con tag propio, fuera de precios y de alertas (§5) |
| Xbox/Game Pass sin identidad apta para precios | El export sí trae `GameId`, pero es package/console id; se guarda para identidad de biblioteca y sigue excluido de precios por `state='subscription'` |

## 13. Fuera de alcance

- **Integraciones por tienda**: Epic (`legendary`), Amazon (`nile`), GOG (OAuth o `galaxy-2.0.db`),
  Ubisoft (demux), Battle.net (cookie de sesión), Xbox (`xbox-webapi`). Todas reversadas, sin soporte y con
  riesgo de romperse. Solo entran si algún día hay que prescindir de la máquina Windows.
- **Entitlements oficiales de Xbox / Game Pass**: partner-only, devuelven solo los productos del publisher.
- Steam por API oficial y `LookupByShopAsync`: §10.
- OAuth con ITAD y su waitlist (sigue descartado en `PLAN_WISHLIST.md`).
- Sincronización automática del export: manual, por decisión.
- Duración de juegos (HowLongToBeat/IGDB): `PLAN_CATALOG.md` §6.
- Ahorro por bundles según juegos poseídos: sigue diferido (`PLAN_BUNDLES.md`, `PLAN_WISHLIST.md` §11).
- Segundo usuario o multiusuario: anclado al admin.

## 14. Verificación

| Fase | Evidencia |
|---|---|
| 0 | Un juego presente en Xbox y en Game Pass produce **dos** filas que coexisten sin violar `uq_user_library`; la de Game Pass muestra tag y ningún precio |
| 1 | Subir el export real: el conteo cuadra con Playnite y el reporte desglosa por tienda; reimportar no duplica ni borra nada |
| 2 | Un juego de Steam liga por appid; uno de GOG/Amazon liga por fusión y muestra el mismo precio que su detalle; un título ajeno al catálogo muestra "Sin precios vinculados" y **ningún** precio |
| 3 | `pnpm build`; un juego en Steam y Amazon aparece **una sola vez** con las dos tiendas |
| 4 | Reseñas múltiples: el mismo juego en dos tiendas admite dos reseñas y el mismo juego dos veces en la misma tienda también; el drawer edita la vieja y agrega otra sin perder ninguna; reimportar el export no las borra; un cambio de estado en `user_library` no las afecta |

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```

## Anexo A: script de export propio (plan B)

Solo si las extensiones listas dejan de servir. Referencia funcional, **no probada en runtime**. Se instala
como script extension en Playnite y se guarda en el repo como documentación operativa, no se ejecuta desde
DealExt.

```powershell
function ExportLibrary {
    param($PlayniteApi)
    $games = $PlayniteApi.Database.Games | ForEach-Object {
        [pscustomobject]@{
            GameId      = $_.GameId
            PluginId    = $_.PluginId.Guid
            SourceName  = $_.Source.Name
            Name        = $_.Name
            IsInstalled = $_.IsInstalled
            Added       = $_.Added
        }
    }
    $path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'dealext-library.json'
    $games | ConvertTo-Json -Depth 4 | Set-Content -Path $path -Encoding UTF8
}
```

`GameId` es el id de tienda y `PluginId`/`SourceName` identifican la tienda de origen: esos dos campos son
el motivo de no usar el export oficial.

## Anexo B: cómo congelar el mapeo de tiendas

Procedimiento de una sola vez, con la máquina que tiene Playnite:

1. Instalar la extensión JSON (§6) y lanzar un export completo.
2. Del JSON, listar pares `(PluginId, SourceName)` únicos con un juego de ejemplo cada uno.
3. Confirmar cada par contra una tienda conocida (Steam por appid, GOG por id numérico, etc.).
4. Guardar la tabla resultante en el código como diccionario `PluginId → store`, con `SourceName`
   normalizado como llave secundaria.
5. Añadir al repo una fixture mínima y saneada del export (sin nombres de usuario ni rutas) para que el
   import tenga un caso de prueba permanente y un caso de "sin binding" reproducible.

El paso 5 es el que evita que el contrato vuelva a desactualizarse en silencio.
