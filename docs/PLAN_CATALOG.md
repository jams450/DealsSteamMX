# DealExt: plan de catálogo canónico de juegos

Estado: propuesto, pendiente de aprobación. Solo análisis y plan.

Alcance: crear una **identidad de juego compartida por todo** —comparador, wishlist, biblioteca y reseñas—
para que la misma ficha de juego se lea en un solo sitio, y para que añadir fuentes futuras
(HowLongToBeat, IGDB) sea **insertar datos y no migrar**. No se renombra `steam_games` ni se re-ancla
`game_offers` a `game_id`.

Continúa a `PLAN_ITAD.md` y `PLAN_IMPLEMENTACION_BIBLIOTECA.md`. Consumidores: `PLAN_LIBRARY.md`
(biblioteca y reseñas) y `PLAN_WISHLIST.md` (wishlist y alertas). Leer también `AGENTS.md` y
`PLAN_BASE_MVP.md`.

## 1. El problema y la decisión

Hoy `steam_games` es **identidad y snapshot regional a la vez**: clave `UNIQUE (app_id, region)` con
`region NOT NULL DEFAULT 'mx'`. Y cuelga de él todo lo demás:

| Tabla | Ancla actual |
|---|---|
| `game_offers` | FK `steam_game_id`, `UNIQUE (steam_game_id, source, offer_key)` |
| `steam_price_observations` | FK `steam_game_id` |
| `external_bundle_games` | FK `steam_game_id` |
| `user_library` | Independiente: `(user_id, store, store_game_id, state)` + `itad_game_id` nullable |

El requisito es que wishlist, biblioteca y reseñas lean **la misma ficha de juego**, y que HowLongToBeat
encaje después sin otro refactor. Eso pide una fila canónica de juego.

### Decisión: capa canónica **al lado** de `steam_games`

Se añaden dos tablas nuevas (`games`, `game_external_ids`) y un `game_id` nullable en `steam_games` y en
`user_library`. Nada más cambia.

### Por qué no hay una clave natural única

| Candidata | Por qué no sirve |
|---|---|
| UUID de ITAD | **Puede ser NULL para siempre.** ITAD no tiene tienda de Amazon (34 tiendas verificadas), así que un juego de Prime puede no tener UUID jamás |
| `app_id` de Steam | **No existe** para un exclusivo de Epic, GOG o Ubisoft |
| Título normalizado | No es identidad: es heurística. Es la causa de los falsos positivos de §4 y por eso hoy el binding se calcula al leer |

Por eso la identidad es **un PK sustituto más una tabla de ids externos**. Es la única forma que sobrevive
a los dos primeros casos.

## 2. Esquema

```sql
-- SQL/migrations/2026-09-26_games_canonical.sql  (+ bloque en SQL/schema.sql)
CREATE TABLE games (
    game_id BIGSERIAL PRIMARY KEY,
    title VARCHAR(512) NOT NULL,
    normalized_title VARCHAR(512) NOT NULL,   -- minúsculas, sin puntuación, sin sufijo de edición
    type VARCHAR(32),
    image_url VARCHAR(512),
    is_free BOOLEAN NOT NULL DEFAULT FALSE,
    release_year INT,                         -- ver §6: HLTB solo da año
    first_release_date DATE,                  -- se guarda preciso para desambiguar remasters
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

-- No es UNIQUE a propósito: el título normalizado sirve para fusionar y para mostrar, nunca para afirmar identidad.
CREATE INDEX idx_games_normalized_title ON games(normalized_title);

-- La identidad dura. namespace reutiliza el vocabulario de user_library.store + 'itad' + 'igdb' + 'hltb'.
CREATE TABLE game_external_ids (
    game_external_id BIGSERIAL PRIMARY KEY,
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    namespace VARCHAR(32) NOT NULL,   -- steam | itad | gog | epic | amazon | xbox | ubisoft | ea | hltb | igdb | wikidata
    external_id VARCHAR(64) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_game_external_ids UNIQUE (namespace, external_id)
);

CREATE INDEX idx_game_external_ids_game ON game_external_ids(game_id);

-- Cambios aditivos en tablas existentes
ALTER TABLE steam_games  ADD COLUMN IF NOT EXISTS game_id BIGINT NULL REFERENCES games(game_id);
ALTER TABLE user_library ADD COLUMN IF NOT EXISTS game_id BIGINT NULL REFERENCES games(game_id);
CREATE INDEX IF NOT EXISTS idx_steam_games_game ON steam_games(game_id);
CREATE INDEX IF NOT EXISTS idx_user_library_game ON user_library(game_id);
```

Notas de diseño:

- `namespace`/`external_id` son `VARCHAR(32)`/`VARCHAR(64)` a propósito: **copian el vocabulario de
  `user_library.store`/`store_game_id`**, así el import de Playnite siembra mapeos sin traducción.
- `uq_game_external_ids (namespace, external_id)` es la identidad real. El `game_id` es lo que se propaga.
- `normalized_title` guarda el valor ya normalizado para que la fusión por título deje de ser un
  `lower(...)` a tabla completa. Reemplaza el índice funcional que `PLAN_LIBRARY.md` dejaba sin construir.
- `release_year` y `first_release_date` existen por §6: el año es la causa principal de falsos positivos
  al emparejar con HLTB, y tener la fecha precisa permite una regla de año ±1 en vez de igualdad exacta.

## 3. Qué pasa con `steam_games` y con `game_offers`

### `steam_games` se queda, pero deja de ser la identidad

Gana `game_id` y conserva `app_id`, `region` y sus columnas de snapshot. `region` **pertenece** a
`steam_games`: es una propiedad de un snapshot de precio, no de un juego.

Se evaluó demolerlo (renombrar a `steam_game_prices`, mover `name`/`type`/`image_url`/`is_free` a `games`,
dejar `app_id` solo como mapeo). Se descarta: obliga a reescribir `SteamGameService` (1400+ líneas), la
entidad, `AppDbContext` y cada referencia a `game.AppId`, **sin ganancia funcional** — el servicio sigue
necesitando el `app_id` para llamar a Steam y a `lookup/id/shop/61`. Lo único que importa de esa demolición
es dejar de tratar el appid como identidad, y eso ya lo consigue el `game_id`.

Las 4 columnas de display quedan duplicadas en `steam_games` como caché del snapshot regional. Es el
trade-off honesto y es barato.

### `game_offers` NO se re-ancla en V1

Tres razones concretas:

1. **El precio depende de la región.** ITAD se consulta por `country` y gg.deals por región; hoy la región
   se hereda implícitamente vía `steam_game_id`. Un ancla directa a `game_id` exigiría una columna
   `region`, o los snapshots de MX y US colisionarían en `(game_id, source, offer_key)` y uno pisaría al
   otro en silencio.
2. **La única vía de precios implementada es el appid de Steam.** Nada en el requisito pide tarificar un
   juego que no esté en Steam.
3. **Hay lógica de refresco atada al appid:** ventana de refresco y un gate por appid en proceso. Moverla
   no aporta nada.

**Receta futura, si algún día hace falta tarificar fuera de Steam:** añadir `game_id BIGINT` y
`region VARCHAR(2)` a `game_offers`, rellenar `game_id` desde `steam_games.game_id`, y estrechar la clave
única a `(game_id, region, source, offer_key)`. La escritura de proveedores no cambia: sigue resolviendo el
appid, que ahora vive en `game_external_ids(namespace='steam')`.

## 4. Cómo se crea la fila canónica

**Clave natural = `uq_game_external_ids (namespace, external_id)`.** Orden de siembra, en el import de
Playnite y en el refresco de Steam:

| Prioridad | Condición | Mapeo que se escribe |
|---|---|---|
| 1 | `store = 'steam'` | `('steam', store_game_id)` — el appid |
| 2 | `itad_game_id` presente | `('itad', itad_game_id)` |
| 3 | cualquier tienda | `(store, store_game_id)` — el export real siempre da un id; un exclusivo de Epic/GOG/Amazon/Xbox sin ITAD y sin appid obtiene identidad dura sin llamada extra a ningún proveedor |
| 4 | coincidencia por título | **Nunca fusiona ni escribe identidad.** Solo puede producir un candidato de precio para lectura (§4.1) |
| 5 | sin `store_game_id` | sin fila `games`; `user_library.game_id` queda `NULL` (= "sin identidad canónica") |

Contrato del resolver, nuevo y en `Deals.BusinessLogic/Services/`:

```csharp
Task<long> ResolveOrCreateGameAsync(
    string title, IReadOnlyList<(string Namespace, string ExternalId)> ids, CancellationToken ct);
```

Comportamiento: inserta en `games` salvo que un mapeo ya lo resuelva; por cada id hace
`INSERT ... ON CONFLICT (namespace, external_id) DO NOTHING` y vuelve a leer. Es el mismo patrón de reclamo
atómico que ya usa la ruta de bundles.

**Sin auto-fusión en V1.** Un mapeo solo se añade a una `games` existente cuando coincide por id externo
exacto (`namespace`, `external_id`) o por UUID ITAD exacto. La coincidencia de título es la parte
genuinamente arriesgada de este diseño —un duplicado se convierte en precio equivocado, reseña equivocada
y badge "ya lo tienes" falso—, por lo que no modifica `games`, `game_external_ids` ni `user_library.game_id`.

### 4.1 Candidato de precio, sin tocar identidad

La biblioteca puede consultar el precio de una entrada sin identidad Steam usando su `title` normalizado:

1. Buscar `steam_games.name` normalizado.
2. Si hay **una sola** candidata, mostrar su precio con la etiqueta `Precio vinculado por título`.
3. Si hay cero o más de una, mostrar `Sin precios vinculados`.

Es un cálculo de lectura. No escribe ids, no agrupa filas de biblioteca y no permite el badge de propiedad.
Una integración futura (`LookupByShopAsync`, IGDB o revisión manual) podrá unir identidades exactas.

Sitios de llamada del resolver: exactamente dos, ambos ya envueltos en transacción — la inserción de juego
nuevo en `SteamGameService` (`SearchAsync` / `PersistSteamSnapshotAsync`) y el import de Playnite.

## 5. Migración, sin EF

Incremental, en archivos fechados, **sin ventana de doble escritura**. `game_id` es derivado y nullable,
con un solo escritor (el resolver); las escrituras actuales a `steam_games` y `user_library` no se tocan.

| Archivo | Contenido |
|---|---|
| `2026-09-26_games_canonical.sql` | Solo DDL: `games`, `game_external_ids`, los dos FK `game_id` nullables e índices |
| `2026-09-26_games_backfill.sql` | Idempotente, SQL puro: una `games` por `app_id` distinto (con `MIN(steam_game_id)` para el título), mapeos `('steam', app_id)` y `('itad', itad_game_id)`, y los `UPDATE` de `steam_games.game_id` y `user_library.game_id` |
| `2026-09-28_game_reviews.sql` | Reseñas — es de `PLAN_LIBRARY.md` §9 |

Reglas del backfill:

- `user_library` con `store='steam'` se liga por `steam_games.app_id::text = store_game_id`; el resto por
  `itad_game_id` contra el namespace `'itad'`. Lo que no resuelva **se queda en `NULL` por diseño**.
- Re-ejecutable sin daño. Si prefieres no tocar `SteamGameService` todavía, re-ejecutar el `UPDATE` del
  backfill periódicamente es el sustituto (mismo patrón que `FxRateRefreshJob`).
- `steam_games.game_id` se puede endurecer a `NOT NULL` cuando un `count(*) where game_id is null` dé cero.
  **`user_library.game_id` nunca: `NULL` significa "sin resolver", no "error".**

**Orden obligatorio:** commitear primero el trabajo de ITAD y FX que sigue staged sin commitear, o las
fechas de `SQL/migrations/` pierden su orden.

## 6. Fuentes futuras: IGDB como espinazo, HLTB como dato opcional

Esta sección existe para que añadir tiempos de duración después sea **aditivo**. No se crea nada de esto
en V1.

### IGDB es el espinazo de identidad

| Aspecto | Detalle |
|---|---|
| API | Oficial: `POST https://api.igdb.com/v4/{endpoint}`, OAuth2 `client_credentials` contra Twitch |
| Licencia | **Gratis solo para uso no comercial**; si DealExt se monetiza hace falta acuerdo de partner |
| Límites | 4 req/s |
| Duración | Endpoint `game_time_to_beats`: `hastily` ≈ solo historia, `normally` ≈ historia + extras, `completely` = 100%. **En segundos** |
| Cross-reference | Su tabla `external_games` enlaza Steam appid, GOG, Epic, itch.io, Xbox — es lo que permite resolver un juego *hacia* los demás |
| Debilidad | **Cobertura dispersa**: el endpoint es de 2025 y su ratio frente al catálogo completo es bajo. Hay que medirlo antes de diseñar encima |

### HLTB: no se puede crawlear, y no se va a crawlear

Hallazgo de cumplimiento, no técnico. El `robots.txt` vigente de HLTB tiene `Disallow: /api` —que es justo
el endpoint de todas las librerías— y sus términos prohíben "crear datasets con nuestro contenido o
compartirlos". Además los endpoints rotan **~2 veces al año** con token atado al fingerprint del
User-Agent y honeypot rotativo: el plugin de Playnite acumula 3 roturas solo en 2026.

Por tanto:

- **Prohibido un job de scraping** sobre el catálogo. Nada de "sincronizar duraciones de toda la biblioteca".
- `hltb_game_id` es **nullable, no autoritativo y de solo-identificador**: llega de Wikidata (propiedad
  P2816), de entrada manual del usuario, o de un resolvedor de IDs de pago. **Nunca se guarda una URL como
  join, sino el id numérico más la fuente.**
- Si algún día entra, entra por **acción puntual del usuario** y debe poder purgarse en un comando.

### Forma de almacenamiento de duraciones

Cuando entre cualquiera de las dos fuentes:

```sql
-- No se crea en V1. Puramente aditiva cuando haga falta.
CREATE TABLE game_completion_estimates (
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    source VARCHAR(32) NOT NULL,            -- 'igdb' | 'hltb' | ...
    source_game_id VARCHAR(64),             -- espejo de game_external_ids
    source_bucket VARCHAR(32) NOT NULL,     -- etiqueta del proveedor, sin traducir
    normalized_bucket VARCHAR(32),          -- main | main_extra | completionist | all_styles | coop | mp | NULL
    seconds INT,                            -- unidad canónica
    sample_count INT,
    lowest_seconds INT,
    highest_seconds INT,
    platform VARCHAR(32),                   -- HLTB lo da por plataforma; IGDB no
    fetched_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (game_id, source, source_bucket, platform)
);
```

Decisiones que conviene no re-litigar:

- **Entero en segundos, no `_minor` ni horas en coma flotante.** La convención `_minor` del repo es para
  **dinero**, no para duraciones. Y los dos proveedores devuelven segundos en crudo: convertir en el borde
  evita la deriva de decimales que tienen las librerías que pasan a horas.
- **No se aplanan los vocabularios.** IGDB tiene 3 cubos y HLTB tiene 7 (incluye all-styles, cooperativo y
  competitivo): no son un mapeo 1:1. `source_bucket` guarda la etiqueta original y `normalized_bucket` es
  el mapeo de presentación, nullable donde no haya mapeo honesto.
- **Sin tabla de histórico.** `fetched_at` más un job de refresco basta.

## 7. Riesgos

| Riesgo | Mitigación |
|---|---|
| El resolver toca `SteamGameService` (dos sitios de inserción) y el import | Patrón `ON CONFLICT` atómico ya probado en la ruta de bundles; ambos sitios ya están en transacción |
| Sin EF: el orden de las migraciones es manual | Commitear ITAD y FX primero (§5) |
| Fusionar dos `games` por título crea duplicados con precios y badges erróneos | **V1 no auto-fusiona**: un falso positivo solo puede dejar una fila sin resolver, jamás un precio equivocado |
| `user_library.game_id` en `NULL` | Es un estado válido y visible: la UI conserva "Sin precios vinculados" |
| Cobertura de `game_time_to_beats` en IGDB | Medir (`/game_time_to_beats/count` contra `/games/count`) antes de diseñar encima |
| Tier no comercial de IGDB | Anotado: monetizar DealExt exige acuerdo de partner |
| Coste de añadir HLTB más adelante | Se evita con §6: es insertar filas, sin migración |

## 8. Fuera de alcance

- Demoler o renombrar `steam_games`.
- Re-anclar `game_offers`, `steam_price_observations` o `external_bundle_games` a `game_id` (receta futura
  en §3).
- Auto-fusión de filas `games` por título.
- Crear `game_completion_estimates` o cualquier integración con IGDB/HLTB.
- Tarificar juegos que no estén en Steam.
- `LookupByShopAsync`: sigue siendo una mejora diferida de `PLAN_LIBRARY.md`.

## 9. Verificación

| Fase | Evidencia |
|---|---|
| 1 | Migración aplicada; `SELECT count(*) FROM steam_games WHERE game_id IS NULL` y el equivalente de `user_library` dan los números esperados; re-ejecutar el backfill no cambia nada |
| 1 | Un juego con appid y UUID de ITAD produce **una** fila `games` con **dos** mapeos |
| 2 | Abrir un detalle de juego nuevo crea una fila `games` y su mapeo `('steam', appid)` sin duplicar la existente |
| 2 | Un título que ITAD no conoce (caso Amazon) obtiene identidad por el paso 3 o queda con `game_id` en `NULL`, y en ningún caso se fusiona en una `games` ajena |

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```
