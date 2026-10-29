# Compra de bundles e importación a biblioteca (T3, S3-S6)

Implementa el backend de `odd/tasks/bundle-expiry-acquisition.md` S3-S6: reportar un bundle como comprado,
quitar el bundle del catálogo pendiente del comprador, importar sus juegos a la biblioteca, registrar
duplicados y limpiar la wishlist de Steam con protección contra el siguiente sync.

Estado: implementado en este cambio. Migración manual **no aplicada**; no hay commit ni push. Los checks
deterministas cubren las reglas puras; lo que necesita PostgreSQL real está listado en §8.

## 1) Decisiones

- Identidad canónica: el UUID de ITAD es identidad de primera clase (`game_external_ids.namespace = 'itad'`,
  resuelto/creado por `GameIdentityResolver`). Un ítem sin identificador de proveedor **exige** un vínculo
  canónico explícito que la persona elige en la búsqueda de candidatos. Un título —y menos un título de
  gg.deals, que no tiene UUID— nunca es identidad.
- Un tier fijo importa toda su membresía elegible (solo `type = 'game'`). Un tier de elección debe nombrar
  exactamente el `selectionCount` publicado.
- Un tier incompleto, addon o sin juegos elegibles se bloquea y no se puede confirmar. Nunca se importa una
  membresía parcial en silencio.
- Plataforma a nivel de tier con override por juego; se resuelve en el servidor.
- Duplicado = mismo juego canónico ya poseído **en esa plataforma** (otra plataforma no es duplicado). El
  duplicado no escribe fila de biblioteca pero sí deja su fila de compra y participa en la limpieza de
  wishlist.
- La compra guarda su propio snapshot del tier comprado **antes** de que el refresh upstream reescriba
  `external_bundles.tiers_json`.

## 2) Tablas (`SQL/schema.sql`, migración `SQL/migrations/2026-10-21_bundle_purchases.sql`)

### `bundle_purchases`

Una fila por `(user_id, source, bundle_key)` — el registro de adquisición y la clave de idempotencia.

| columna | tipo | notas |
|---|---|---|
| `bundle_purchase_id` | BIGSERIAL PK | |
| `user_id` | INT NOT NULL → `users` CASCADE | dueño de la compra |
| `source`, `bundle_key` | VARCHAR(16/128) | fuente del proveedor y clave del bundle |
| `title`, `shop_id`, `shop_name`, `page_url`, `deal_url` | | snapshot de metadata |
| `published_at`, `expires_at`, `observed_at` | TIMESTAMPTZ | snapshot de tiempos del proveedor |
| `tier_key` | VARCHAR(64) NOT NULL | sha256 hex del contenido del tier confirmado |
| `tier_index` | INT | posición del tier en el snapshot |
| `tier_price_minor`, `tier_currency` | INT / VARCHAR(3) | precio publicado del tier |
| `tier_addon`, `tier_items_complete` | BOOLEAN | reglas de importabilidad |
| `tier_selection_count` | INT NULL | "elige N de M"; NULL en tier fijo |
| `selection_mode` | VARCHAR(8) | `fixed` \| `choice` (CHECK) |
| `selected_count` | INT | tamaño de la membresía confirmada (> 0) |
| `platform` | VARCHAR(32) | plataforma del tier |
| `acquired_at` | DATE NULL | fecha de compra declarada |
| `acquired_store`, `acquired_store_type`, `acquisition_provider` | | metadatos opcionales, vocabularios cerrados |
| `snapshot_hash` | VARCHAR(64) NOT NULL | el hash que el confirm tuvo que repetir |
| `snapshot_json` | JSONB NOT NULL | proyección canónica del tier completo (ítems incluidos) |

CHECKs: `selection_mode` cerrado; `fixed` ⇔ `tier_selection_count IS NULL`; `selected_count > 0`; hash,
tier key y plataforma no en blanco; clasificación/proveedor requieren `acquired_store`.
Sin FK a `external_bundles` a propósito: la compra es evidencia y sobrevive una purga del proveedor.
Sin índice extra por `user_id`: la clave única ya empieza por esa columna.

### `bundle_purchase_games`

Una fila por ítem seleccionado.

| columna | tipo | notas |
|---|---|---|
| `bundle_purchase_game_id` | BIGSERIAL PK | |
| `bundle_purchase_id` | BIGINT NOT NULL → `bundle_purchases` CASCADE | |
| `item_key` | VARCHAR(128) | `itad:<uuid>` o `item:<hash16>`; único por compra |
| `title`, `item_type`, `itad_game_id` | | datos del ítem |
| `game_id` | BIGINT NOT NULL → `games` **NO ACTION** | una fusión lo repunta, nunca lo borra |
| `canonical_source` | VARCHAR(8) | `itad` \| `manual` (CHECK) |
| `platform` | VARCHAR(32) | plataforma final del juego |
| `outcome` | VARCHAR(16) | `imported` \| `duplicate` (CHECK) |
| `user_library_id` | BIGINT NULL → `user_library` SET NULL | fila creada o ya existente |
| `wishlist_removed` | BOOLEAN | la exclusión queda registrada en `wishlist_exclusions` |

Índice `idx_bundle_purchase_games_game (game_id)` para el FK de la fusión.

**Fusión de juegos:** `GameMergeService` repunta `bundle_purchase_games.game_id` antes del
`DELETE FROM games`, sin borrar membresías adquiridas. El FK `NO ACTION` protege la evidencia y el
check de composición verifica ese orden. Ejecutar `SQL/checks/bundle_purchases.sql` y
`SQL/checks/merge-invariants.sql` después de aplicar la migración y después de las pruebas de compra,
borrado y fusión en una base controlada: cada consulta debe devolver cero filas. Estos checks complementan,
pero no sustituyen, una prueba real de confirmaciones concurrentes y locks en PostgreSQL.

## 3) Endpoints (`Deals.API/Controllers/BundlesController.cs`, `[Authorize UserWithId]`, rate limit `steam-read`)

El usuario sale siempre de `ICurrentUserService`; ningún endpoint acepta un user id del cliente.

### `GET /api/bundles/purchases/preview?source=itad&bundleKey=<key>&platform=steam`

`platform` es opcional: sin ella `owned` es `null` ("desconocido"), nunca `false`.

```json
{
  "source": "itad", "bundleKey": "bundle-1", "title": "Prestige Collection",
  "shopId": "4", "shopName": "Fanatical", "pageUrl": "https://...", "dealUrl": "https://...",
  "details": "...", "publishedAt": "2026-10-01T00:00:00Z", "expiresAt": null,
  "observedAt": "2026-10-18T12:00:00Z",
  "snapshotHash": "<64 hex>", "purchased": false, "purchaseId": null,
  "tiers": [
    {
      "tierKey": "<64 hex>", "tierIndex": 0, "priceMinor": 1499, "currency": "USD",
      "addon": false, "itemsComplete": true, "selectionCount": null,
      "selectionMode": "fixed", "requiredSelectionCount": 3,
      "importable": true, "blockedReason": null,
      "items": [
        {
          "itemKey": "itad:7b1b2f14-...-a", "title": "Alpha", "type": "game",
          "priceMinor": 1000, "currency": "USD", "itadGameId": "7b1b2f14-...-a",
          "url": null, "eligible": true, "canonicalGameId": 11, "canonicalSource": "itad",
          "requiresManualLink": false, "owned": true, "ownedUserLibraryId": 45,
          "ownedPlatform": "steam"
        }
      ]
    }
  ]
}
```

`blockedReason`: `null` | `items_incomplete` | `addon_tier` | `no_eligible_games` |
`invalid_selection_count`. Un tier bloqueado no es confirmable.

### `POST /api/bundles/purchases`

```json
{
  "source": "itad", "bundleKey": "bundle-1", "tierKey": "<64 hex>", "platform": "steam",
  "snapshotHash": "<64 hex>", "acquiredAt": "2026-10-18",
  "acquiredStore": "fanatical", "acquiredStoreType": "official", "acquisitionProvider": "itad",
  "items": [
    { "itemKey": "itad:7b1b2f14-...-a", "platform": null, "canonicalGameId": null },
    { "itemKey": "item:ab12cd34ef56a789", "platform": "gog", "canonicalGameId": 321 }
  ]
}
```

200:

```json
{
  "purchaseId": 3, "created": true, "alreadyReported": false,
  "source": "itad", "bundleKey": "bundle-1", "title": "Prestige Collection",
  "tierKey": "<64 hex>", "platform": "steam", "snapshotHash": "<64 hex>",
  "selectedCount": 2, "importedCount": 1, "duplicateCount": 1, "wishlistRemovedCount": 2,
  "games": [
    { "itemKey": "itad:...", "title": "Alpha", "gameId": 11, "platform": "steam",
      "outcome": "imported", "userLibraryId": 45, "canonicalSource": "itad",
      "wishlistRemoved": true }
  ],
  "duplicates": [
    { "purchaseId": 3, "source": "itad", "bundleKey": "bundle-1",
      "bundleTitle": "Prestige Collection", "acquiredAt": "2026-10-18",
      "itemKey": "itad:...", "title": "Beta", "gameId": 12, "platform": "steam",
      "userLibraryId": 77, "existingTitle": "Beta" }
  ]
}
```

Reglas de validación (todas revalidadas en el servidor, 400 con mensaje en español):

- `source`/`bundleKey` deben coincidir con el snapshot; el tier debe seguir existiendo (`tierKey`).
- `snapshotHash` debe coincidir con el hash actual; si el proveedor reescribió el snapshot, hay que
  previsualizar de nuevo.
- Tier fijo: la selección debe ser exactamente la membresía elegible. Sin `items` se toma la membresía
  completa **solo si** todos sus juegos tienen UUID de ITAD.
- Tier de elección: exactamente `selectionCount` ítems, sin repetidos, todos elegibles.
- Ítem con UUID de ITAD: `canonicalGameId` debe ser `null`.
- Ítem sin UUID: `canonicalGameId` obligatorio y debe existir en `games`.
- `platform`/override normalizados con `StoreKeys`; fecha no futura; vocabularios cerrados.
- Un tier bloqueado (incompleto/addon/sin juegos) no se confirma.

Repetición: misma `(user, source-family, bundle_key)` ⇒ 200 con `created = false`,
`alreadyReported = true`, mismos datos y **cero escrituras**.

### `GET /api/bundles/purchases`

`{ "items": [<registro de compra con games>], "count": N }`. Incluye `snapshotHash`, datos del tier,
`importedCount`, `duplicateCount` y la membresía. Orden: más reciente primero.

### `GET /api/bundles/purchases/duplicates`

`{ "items": [<BundlePurchaseDuplicateResponse>], "count": N }`. Solo las compras y filas del llamador;
`existingTitle` viene de la fila de biblioteca que ya tenía el juego.

### `GET /api/bundles/catalog-candidates?q=<título>&limit=10`

Búsqueda de solo lectura sobre `games.normalized_title`. `limit` se acota a 1..20 (default 10).
`{ "items": [{ "gameId": 12, "title": "...", "releaseYear": 2019, "imageUrl": null, "inLibrary": true }], "count": 1 }`.
No crea juegos y no elige nada: el cliente manda el `canonicalGameId` en el confirm.

### `GET /api/bundles` (existente)

Mismo contrato; los bundles ya comprados por el llamador se excluyen **antes** de deduplicar y paginar, así
que `total`, las páginas y el orden describen solo lo pendiente. La ocultación usa la familia de fuente:
una compra `itad` oculta también el snapshot `itad_global` del mismo `bundle_key`; gg.deals es su propio
espacio de claves.

## 4) Transacción, orden de locks e idempotencia

Una sola transacción (`IRepository.ExecuteInTransactionAsync`):

1. `WishlistUserLock.AcquireAsync(userId)` — misma clave por usuario que usa el sync de wishlist y la
   adquisición de wishlist. Serializa la escritura de filas wished y exclusiones.
2. `GameIdentityLock.AcquireAsync()` — serializa con toda escritura de identidad canónica (resolver,
   import, merge).
3. Lectura del snapshot y de la compra existente (replay ⇒ respuesta, sin escrituras).
4. Validación pura (`BundlePurchaseRules.Plan`).
5. `INSERT bundle_purchases` (snapshot congelado, `UNIQUE (user_id, source, bundle_key)`).
6. Por ítem: resolver identidad (`itad` namespace o vínculo manual) → decidir duplicado contra
   `user_library` de esa plataforma → insertar fila owned si corresponde (`store_game_id` = id canónico
   sintético; la deduplicación es por juego canónico + plataforma).
7. Wishlist: mapear juego canónico → appid de Steam (`game_external_ids` primero, `steam_games` después;
   nunca desde el título), borrar las filas wished y crear las exclusiones que falten.
8. `INSERT bundle_purchase_games` con `outcome` y `wishlistRemoved`.

El lock por usuario es lo que hace que un segundo envío concurrente no pueda insertar dos veces; la clave
única es la garantía de esquema. Sin PostgreSQL real no se puede probar la carrera (§8).

## 5) Wishlist e inventario de duplicados

- Se quitan **todos** los juegos adquiridos que resuelvan a un appid wished, incluidos los duplicados.
- Cada appid quitado deja una fila en `wishlist_exclusions`, así que `WishlistSyncService` no lo readmite.
- `wishlist_removed` en `bundle_purchase_games` refleja ese hecho; no se inventa appid para un juego sin
  identidad Steam.
- Un duplicado registra `outcome = 'duplicate'` con la fila de biblioteca existente, uno por juego
  comprado. Otra plataforma no es duplicado: el juego se importa.

## 6) Metadata de `user_library` creada por la compra

`store = platform`, `state = 'owned'`, `game_id` canónico.

`store_game_id` no inventa una identidad de tienda:

- si la tienda elegida ya conoce un id exacto del juego (hoy el appid de Steam), se usa tal cual;
- Steam sin appid conocido usa `bundle:<game_id>`; un id canónico “pelado” ahí se leería como appid y
  reclamaría una identidad de Steam que el catálogo nunca probó (`LibraryPriceBindingService` interpreta el
  `store_game_id` de una fila `steam` como appid);
- cualquier otra plataforma usa el texto del id canónico, la misma forma que escriben el import manual y el
  de consola.

`acquisition_type = 'bundle'` **solo** cuando el confirm trae `acquiredStore` (el CHECK del esquema exige
tienda para cualquier metadata); con `acquiredStore` ausente la fila queda sin metadata y la compra conserva
sus propios datos. El `acquired_at` de la compra se guarda siempre en `bundle_purchases`.

## 7) Notificaciones (handoff, no implementado aquí)

Supresión previa al dispatch de un recordatorio rojo:

```sql
SELECT 1
FROM bundle_purchases
WHERE user_id = @userId
  AND bundle_key = @bundleKey
  AND (source = @source OR (source IN ('itad','itad_global') AND @source IN ('itad','itad_global')));
```

La compra se confirma antes de que el worker encolaría el aviso, así que un bundle comprado no dispara el
recordatorio rojo. No se tocó código de notificaciones.

## 8) Verificación y límites

Determinista (sin base de datos):

```bash
dotnet run --project checks/BundlePurchase.Checks      # reglas puras S3-S6
dotnet run --project checks/BundleCatalog.Checks       # catálogo existente intacto
dotnet run --project checks/WishlistAcquisition.Checks # adquisición de wishlist intacta
dotnet build Deals.sln
git diff --check
```

Checks cubiertos: hash estable y tampering/estaleness, tier fijo vs elección, conteo, membresía, ítems
inelegibles, tier incompleto/addon/vacío, vínculo manual obligatorio y validado, UUID de ITAD que rechaza un
id del cliente, overrides de plataforma, fecha futura, ocultación por familia de fuente, y plan de wishlist
(remociones, exclusiones nuevas, duplicados, juego sin appid).

No probable en checks deterministas (divulgado): el orden de locks y su 409, la carrera real de dos confirm
concurrentes contra `UNIQUE (user_id, source, bundle_key)`, las cascadas/CHECK reales y la aplicación de la
migración. La evidencia proporcionada para esas partes es la migración manual, `SQL/checks/bundle_purchases.sql`
(9 invariantes, cada una debe devolver 0 filas) y el build.

Para aplicar en una base existente:

```bash
psql "$DATABASE_URL" -f SQL/migrations/2026-10-21_bundle_purchases.sql
psql "$DATABASE_URL" -f SQL/checks/bundle_purchases.sql   # esperado: 0 filas en cada consulta
```

`SQL/checks/bundle_purchases.sql` incluye además la invariante de que un juego marcado `wishlist_removed`
tiene su exclusión, y la de que un import no puede repetir el mismo juego/plataforma dentro de una compra.
