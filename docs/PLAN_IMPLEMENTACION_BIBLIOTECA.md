# DealExt: fases de implementación — biblioteca

Estado: propuesto. Este documento es el orden ejecutable de `PLAN_LIBRARY.md`, `PLAN_CATALOG.md` y
`PLAN_WISHLIST.md`; no sustituye sus contratos.

Objetivo: entregar primero una biblioteca útil desde el export real de Playnite, y posponer el refactor
canónico hasta que habilite reseñas e identidad exacta. No se implementan dos fases a la vez.

Referencias: `PLAN_LIBRARY.md`, `PLAN_CATALOG.md`, `PLAN_WISHLIST.md` y `PLAYNITE_EXPORT.md`.

## 1. Decisiones de orden

| Decisión | Razón |
|---|---|
| Import antes del catálogo canónico | `user_library.game_id` es derivado y nullable; importar no requiere DDL, y Steam + candidato por título ya dan valor |
| Catálogo antes de reseñas | `game_reviews.game_id` es obligatorio; una reseña no puede colgar de una fila de import mutable |
| Binding después del catálogo | El binding por appid/título puede existir antes, pero la identidad exacta y los joins comunes requieren `games` |
| Alertas al final | Requieren wishlist ya existente y una biblioteca importada para excluir poseídos/suscripciones |
| Sin auto-fusión por título | Un falso positivo no puede crear precio, reseña o badge incorrecto |

## 2. Fase 0: cerrar el estado actual

**Entregable:** repositorio base estable; nada visible.

- Confirmar y commitear comparador, FX, wishlist, migración
  `SQL/migrations/2026-09-25_wishlist_min_viable_discount.sql` y el movimiento de documentos a `docs/`.
- No crear migraciones de biblioteca antes de ese commit: las fechas manuales de `SQL/migrations/` deben
  conservar orden.

**Gate:**

```bash
git status --short
dotnet build Deals.sln
cd Deals.Web && pnpm build
```

`git status --short` debe estar limpio tras el commit. Fuera: toda funcionalidad de biblioteca.

## 3. Fase 1: importar Playnite y biblioteca básica

**Entregable visible:** admin sube el export → `/library` muestra juegos por tienda, reporte de import y tag
**Game Pass**. No muestra precios aún.

### Base de datos

Cero DDL. Se reutiliza `user_library` y su clave:

```text
(user_id, store, store_game_id, state)
```

### Backend

- `POST /api/library/import` con política `AdminWithId`.
- Aceptar raíz de arreglo con `GameId`, `PluginId`, `Source`, `Name`, `IsInstalled`, `Added`.
- Aceptar `Added` ISO o `\/Date(<epoch-milisegundos>)\/` de Playnite.
- Validar el mapa Source/PluginId congelado en `PLAN_LIBRARY.md` §6.
- `Xbox` → `state='subscription'`; demás fuentes del contrato → `state='owned'`.
- Upsert idempotente; sin prune; descartar el archivo tras procesarlo.
- `GET /api/library` con agrupación/lista básica por tienda.
- Respuesta de import: `imported`, `updated`, `unresolved`, `unsupportedSource`, `byStore`.
- Crear una única fuente de constantes `LibraryStates`: `wished`, `owned`, `subscription`.

### Frontend

- `/library`, BFF de lectura/import y entrada de navegación.
- Subida del JSON, reporte y filtros por tienda.
- 348 entradas Xbox con tag `Game Pass`; sin precio, alerta ni badge de propiedad.
- Nota `Sin precios vinculados` para todo juego no Steam durante esta fase.

### Gate

- Import real: 2586 entradas recibidas, sin campos críticos faltantes.
- Reimport: no duplica ni borra filas.
- Xbox: 348 filas `subscription`, sin precio ni "Ya lo tienes".
- `dotnet build Deals.sln` y `cd Deals.Web && pnpm build`.

**Fuera:** `games`, precios en biblioteca, reseñas, badges de detalle, alertas.

## 4. Fase 2: catálogo canónico y backfill

**Entregable visible:** ninguno. Entrega identidad compartida interna para reseñas, binding exacto y futuras
fuentes.

### Base de datos

- `2026-09-26_games_canonical.sql`: `games`, `game_external_ids`, FKs `game_id` nullables en
  `steam_games` y `user_library`, índices.
- `2026-09-26_games_backfill.sql`: idempotente.

Regla extra obligatoria porque el import se hizo antes:

```sql
-- Esquema conceptual; la migración final debe resolver primero la fila games correcta.
INSERT INTO game_external_ids (game_id, namespace, external_id)
SELECT game_id, store, store_game_id
FROM user_library
WHERE game_id IS NOT NULL
ON CONFLICT (namespace, external_id) DO NOTHING;
```

El backfill debe crear/relacionar primero una fila `games` para **cada** `(store, store_game_id)` de
`user_library`, no solo para `steam_games.app_id` ni UUID de ITAD. Así las 1418 entradas no-Steam mantienen
identidad canónica aunque nunca tengan precio.

### Backend

- Entidades/DbSets de `games` y `game_external_ids`.
- `ResolveOrCreateGameAsync` atómico.
- Llamar al resolver desde import y desde las inserciones de `SteamGameService`.
- Sin auto-fusión por título; solo IDs externos exactos o UUID ITAD exacto unen identidades.

### Gate

- Backfill re-ejecutable: segunda ejecución no cambia filas.
- Un juego Steam con appid + UUID ITAD tiene una `games` y dos IDs externos.
- El importado Epic/GOG/Amazon/Xbox obtiene su mapping `(store, GameId)`.
- `user_library.game_id = NULL` solo para filas que realmente no tengan identidad importable.

**Fuera:** re-anclar `game_offers`, auto-fusión, IGDB/HLTB.

## 5. Fase 3: binding de precios en biblioteca

**Entregable visible:** precios en entradas ligadas, etiqueta de candidato y nota explícita cuando no haya
binding.

### Base de datos

Cero DDL.

### Backend

Resolver en este orden:

1. `subscription` → no buscar ni devolver precio.
2. Id Steam exacto → `steam_games` → `game_offers`.
3. UUID ITAD exacto → `steam_games` → `game_offers`.
4. Una única coincidencia por título normalizado → precio **read-only**.
5. Cero o varias coincidencias → sin precio.

El paso 4 nunca persiste ID, nunca fusiona `games`, nunca agrupa bibliotecas y nunca autoriza un badge de
propiedad.

### Frontend

- Precio actual, descuento y mínimo histórico cuando haya identidad exacta.
- `Precio vinculado por título` cuando aplique el paso 4.
- `Sin precios vinculados` para los demás.
- Game Pass sin precio aunque exista coincidencia.

### Gate

- Steam: mismo precio que `/games/<appid>`.
- Candidato único: etiqueta visible.
- Ambigüedad: ningún precio.
- Game Pass: ningún precio.

**Fuera:** precios para exclusivos no-Steam, `LookupByShopAsync`.

## 6. Fase 4: badges de propiedad en detalle

**Entregable visible:** detalle Steam informa propiedad sin falsos positivos.

- Exacto: `Ya lo tienes en GOG` / tienda correspondiente.
- `subscription`: tag `Game Pass`, nunca "Ya lo tienes".
- Candidato por título: `Posible coincidencia`, nunca confirmación.

**Gate:** probar las tres variantes con datos del export. Sin cálculo de ahorro.

## 7. Fase 5: reseñas por plataforma

**Entregable visible:** todas las reseñas de un juego, una por partida.

### Base de datos

`2026-09-28_game_reviews.sql` (+ `2026-09-30_game_reviews_multiple.sql`, `2026-10-01_play_status_and_favorites.sql`):

```text
(user_id, game_id, platform)  -- sin UNIQUE: un juego rejugado tiene una reseña por partida
started_month, finished_month, score 0..100, is_goty, body
status: finished | completed | dropped   -- obligatorio; "por jugar" es la ausencia de reseña
user_game_favorites(user_id, game_id)    -- favorito del juego, no de la partida
```

### Backend y frontend

- Servicio con validación: score 0–100, fin ≥ inicio, fechas al día 1 del mes.
- CRUD/BFF/UI de reseñas dentro de biblioteca/detalle canónico. El drawer del juego lista todas las
  reseñas de la plataforma y permite editar una, borrar otra y agregar una nueva; la grilla pinta la más
  reciente (`newestReview`).
- `ReviewScoreBands.Label` es la única fuente de los rangos:
  malo, flojo, regular, bueno, muy bueno, obra maestra.
- La biblioteca filtra por tienda, por año jugado (con conteo por año) y por estado de juego (con conteo por
  estado); el favorito es una estrella por fila que cambia el juego completo.

### Gate

- Mismo juego en dos plataformas: dos reseñas.
- Misma plataforma rejugada: dos reseñas, ambas visibles en el drawer, y se edita la vieja sin tocar la nueva.
- Reimport y cambio `state` no borran ni rompen reseñas.
- Editar una reseña sin estado se rechaza; el filtro de año cuenta los dos años de un juego rejugado.

**Fuera:** score de crítica/comunidad y duración IGDB/HLTB.

## 8. Fase 6: alertas y Telegram

**Entregable visible:** una alerta Telegram por oferta alcanzable.

- `price_alerts` y `PriceAlertScheduler`.
- Regla de elegibilidad: **solo** `user_library.state='wished'`.
- Ni `owned` ni `subscription` pueden alertar.
- Reutilizar `history_low_*` ya persistido.

**Gate:** una alerta se emite una vez; poseído y Game Pass no alertan.

## 9. Dependencias

```text
Fase 0 ─┬─ Fase 1 Import + biblioteca básica ─ Fase 3 Binding ─ Fase 4 Badges
         │                                     └──────────────── Fase 6 Alertas
         └─ Fase 2 Catálogo canónico ───────────────────────────── Fase 5 Reseñas

Fase 3 requiere Fase 1 + Fase 2.
Fase 4 requiere Fase 3.
Fase 5 requiere Fase 2.
Fase 6 requiere Fase 1 + wishlist existente.
```

## 10. Fuera de esta secuencia

- Auto-fusión por título.
- `LookupByShopAsync` y precios de exclusivos no-Steam.
- Re-anclar `game_offers` a `game_id`.
- Integraciones directas por tienda o sincronización automática de Playnite.
- IGDB, HowLongToBeat, puntuaciones externas y ahorro por bundles.
