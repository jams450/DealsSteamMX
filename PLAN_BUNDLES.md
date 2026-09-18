# DealExt: plan de bundles externos

Estado: **V1.1 implementado** (ITAD: display + comparación de precio por tier). No pertenece al comparador de precios ya implementado (Steam + ITAD + gg.deals + FX) y no debe regresarlo.

Implementado: `IItadClient.GetBundlesAsync` (`games/overview/v2?country=MX`, header, lotes de 200, mismo governor/retry), parseo defensivo, bundle canónico `external_bundles` UNIQUE(source, bundle_key) + relación `external_bundle_games` (upserts atómicos `ON CONFLICT`), `steam_games.bundles_refreshed_at` con stale propio, purga ITAD aislada, y bloque UI separado con tiers/precio/expiración/enlaces verbatim y atribución. Comparación por tier según §D.1 (precios individuales ITAD, derivación en lectura, estados y razones).

Pendiente bloqueante de release: **no hay respuesta real guardada** de `overview/v2` para MX; sin fixture sanitizada no se certifica el parser ni se cierra §B. `§C` (gg.deals) sigue fuera de V1 y por eso Build Your Own no está cubierto.

Documento de diseño; nada aquí está verificado contra una respuesta real salvo lo que se marca explícitamente.

Referencias: `AGENTS.md`, `PLAN_BASE_MVP.md`, `PLAN_ITAD.md`, `PLAN_GGDEALS.md`, `PLAN_WISHLIST.md`. Este plan es la continuación diferida de los bundles que `PLAN_ITAD.md` §7 y `PLAN_GGDEALS.md` §10 declaran fuera de alcance.

## A) Alcance y decisiones

1. **Fuente de V1: ITAD, no gg.deals.** ITAD expone bundles con tiers, contenido y expiración; gg.deals queda como fase 2 (ver §C).
2. **Fase separada del comparador.** La comparación de ofertas actual no se toca. Un bundle no entra en `bestOfficial` / `bestAuthorized` / `bestKeyshop` ni en el "mejor precio comparable". Si el trabajo de bundles obliga a cambiar el comparador, se detiene y se revisa el plan.
3. **Sin scraping, HTML ni Playwright.** Solo API. Igual que el resto del repositorio (`PLAN_BASE_MVP.md` §15).
4. **Sin secretos en el frontend.** La key de ITAD vive en el backend (header, nunca en query ni en código cliente). El BFF sigue siendo el único camino.
5. **Sin ahorro basado en biblioteca poseída en V1.** "Lo que ahorrarías porque ya tienes parte del bundle" depende de `user_library`, que es el plan de `PLAN_WISHLIST.md`. En V1 no se cruza ni se insinúa.
6. **El bundle no es una oferta.** Es un paquete con varios tiers, varios ítems y una fecha de expiración. Se muestra como bloque propio, no como fila de la tabla de ofertas.

## B) Contrato ITAD verificado

Endpoint de lote elegido para V1:

```http
POST https://api.isthereanydeal.com/games/overview/v2?country=MX
Content-Type: application/json
ITAD-API-Key: <key>

["<itad-gid-1>", "<itad-gid-2>", ...]
```

- El cuerpo es un array de **gids de ITAD** (los mismos UUID que ya se persisten en `steam_games.itad_game_id`), máximo **200** por llamada.
- Devuelve los **bundles activos que contienen los juegos consultados**. No es un catálogo general de bundles.
- La autenticación es la estándar de ITAD. El cliente existente (`ItadClient`) ya envía la key por header `ITAD-API-Key`, nunca en query; el nuevo método debe seguir ese patrón.

### B.1) Confirmación en vivo (respuesta real de MX)

Una respuesta real de `country=MX` confirmó el esquema y cerró las incógnitas:

- Bundle real observado: Fanatical id `16557`, *"Prestige Collection - Build your Own Bundle (Fall 2026)"*, `counts {games: 28, media: 1}`, un solo tier, `addon: false`, `publish`/`expiry` con offset (activo), `note: null`.
- Raíz objeto con `prices[]` y `bundles[]`; `bundles[]` es un **array plano** de bundles activos (no agrupado por juego).
- Ítems en `tiers[].games[]`: el identificador es **`id`** (UUID), no `gid`, y traen además `slug`, `title`, `type`, `mature`, `assets`. `type` observado: `game` y `package`.
- **`counts.games` coincide con el número de ítems listados en los tiers**; `counts.media` **no aparece** en `tiers[].games[]` (en el caso observado, `media: 1` y ningún ítem media en la lista). `media > 0` no invalida el contenido listado, pero tampoco se puede comparar.
- **`price` puede venir `null` con la clave presente** — conforme a spec, no es un defecto del consumidor. Caso real y frecuente: los bundles **Build your Own** de Fanatical tienen precio dinámico (según cuántos juegos elijas), así que ITAD no publica un precio único de tier. Su propia ficha pública tampoco muestra línea de `Price` en ese caso.
- Los ítems `package` no tienen precio individual en `prices/v3` → quedan fuera de cualquier suma (no son comparables).

Endpoint alternativo descartado para el lote de V1:

```http
GET https://api.isthereanydeal.com/games/bundles/v2?id=<itad-gid>
```

- Es **un gid por request**; con varios juegos multiplica llamadas contra el presupuesto compartido. No se usa para V1. Puede reevaluarse si `overview/v2` resulta insuficiente.

Endpoint `bundles/v1`: **no aporta nada al precio** y no es una alternativa.

- Devuelve el mismo `obj.bundle` (mismo `$ref`), con `tiers[].price` igual de nulable. Migrar no arreglaría un precio ausente.
- Es un **catálogo paginado** (`offset`/`limit` ≤ 50, sin parámetro de ids): no asocia juego→bundle, así que responder "bundles de este juego" obligaría a paginar el catálogo entero y cruzar en local.
- Solo sirve como contraste de diagnóstico, no como fuente de V1.

Esquema confirmado:

- bundle: `id`, `title`, `page` (`id`/`name`/`shopId`), `url`, `details`, `isMature`, `publish`, `expiry` (nulable), `note`, `counts`, `tiers`.
- tier: `price` (`oneOf` precio/nulo, clave siempre presente), `addon`, `games[]`.
- **Regla de atribución:** la `url` (que incluye el tag de afiliado de ITAD) se guarda y se renderiza **verbatim**, nunca se reescribe ni se limpia. La atribución a IsThereAnyDeal debe ser un **hipervínculo activo** visible, igual que en el comparador (`Deals.Web/DESIGN.md` §8, §11). `details` es la página de ITAD y no está en `required`.

Incógnitas a resolver contra la respuesta real antes de fijar el contrato (decision gate de la fase 1):

| Incógnita | Por qué importa |
|---|---|
| Moneda que devuelve `country=MX` | ITAD sirve USD para MX en `prices/v3`; los bundles podrían venir en otra moneda. Define si hay conversión FX o no. |
| Semántica de `addon` | Un ítem marcado como addon no es un juego comparable. Hasta confirmarlo, el tier con addon no calcula ahorro. |
| Relación `counts.games` vs contenido de los tiers | Un conteo no es una lista. Solo una lista completa y determinada habilita el cálculo. |
| Cobertura y `type` de cada ítem | `type` distinto de juego (DLC, soundtrack, pack) no es comparable contra un precio de juego. |
| Forma de expiración/publicación | Define la regla "activo / no expirado" y el orden de presentación. |

## C) gg.deals: fase 2, explícitamente NO V1

Endpoint de referencia:

```http
GET https://api.gg.deals/v1/bundles/by-steam-app-id/?ids=<csv>&key=<key>&region=us
```

- La documentación está verificada, pero el **contrato runtime de fase 2 necesita una muestra real con una API key**. No se declara no verificado el endpoint; lo que falta es una respuesta de ejemplo con key para cerrar el parser.
- `ids` acepta hasta **100** Steam App IDs. `region=us`; **no existe región `mx`**, igual que en el agregado de precios.
- Los tiers pueden ser *Build Your Own*: `gamesCount` no nulo en ese caso.
- No entrega un **id de bundle estable** y los precios llegan como strings (mismo formato que `prices/by-steam-app-id`).
- El endpoint está **activo**; su uso es opcional y de descubrimiento.
- Es **adicional, no sustituto**: cubre bundles vía Steam App ID que ITAD podría no exponer para el mismo juego.
- Restricciones de uso:
  - tier gratuito = uso personal/hobby/open-source; **no comercial**. Aplica igual que para el agregado de precios (`PLAN_GGDEALS.md` §1).
  - la key va en **query param** (única forma soportada), así que la mitigación de logs ya existente (filtro de `System.Net.Http.HttpClient` a `Warning`, nunca la URL completa en mensajes de error) es obligatoria también aquí.

## D) Reglas de ahorro (todas estrictas)

El ahorro de un tier solo se calcula si se cumplen **todas** estas condiciones:

1. El bundle está **activo** y no expirado a la fecha de observación.
2. El tier tiene **precio y moneda**.
3. El contenido está **completamente determinado** (lista completa de ítems, no un conteo).
4. **No es Build Your Own**.
5. **No es addon** hasta que su semántica esté confirmada contra la respuesta real.
6. La lista de ítems incluidos está **completa**.
7. Cada ítem incluido y comparable tiene precio actual vigente **del mismo proveedor/fuente** y en la **misma moneda** (o, si hay conversión, exactamente **una conversión FX claramente etiquetada**).
8. Se **excluyen** ítems desconocidos, paquetes y DLC hasta que el contrato los soporte.
9. **Nunca** se usan mínimos históricos como precio de comparación.
10. **Nunca** se mezclan en silencio precios de ITAD, gg.deals, Steam o keyshops entre sí.

Si cualquier condición falla: **se muestra el bundle, sin cifra de ahorro**, con una razón mínima y legible ("Sin cálculo: contenido incompleto", "Sin cálculo: Build Your Own", "Sin cálculo: moneda distinta", etc.). No se inventa un número ni se muestra un ahorro parcial como si fuera total.

### D.1) Contrato de comparación implementado (V1.1)

El ahorro se **deriva en lectura**; nunca se persiste como verdad.

- Ambas partes salen de **ITAD**: el precio del tier y el precio actual individual de cada ítem del tier (mínimo `CurrentPriceMinor` entre los deals de ese id). Prohibido comparar contra Steam, gg.deals o keyshops.
- `status` expuesto: `ok` (comparación válida) | `no_saving` (no comparable).
- `reason` cuando `status = no_saving`: `no_tier_price`, `addon`, `items_incomplete`, `item_unpriced`, `not_comparable`, `currency_mismatch`, `stale_snapshot`.
- `ok` **no** implica que el bundle sea más barato: `savingsMinor = individualTotalMinor - bundlePriceMinor` puede ser 0 o negativo, y la UI no debe usar tono de éxito en ese caso.
- Ítems que no son juegos (`dlc`/`package`/`unknown`) ⇒ `not_comparable`, sin cifra.
- Cualquier truncado o descarte que vuelva incompleta la lista de ítems (cap de parseo, cap de persistencia, entrada saltada por el parser) ⇒ `items_incomplete`, sin cifra. Ninguna suma parcial puede presentarse como total del tier.
- Snapshot de bundles stale ⇒ ningún tier puede ser `ok` (`stale_snapshot`): se muestra el bundle sin cifra de ahorro.
- MXN solo con **una** conversión FX etiquetada (`pricingType = "fx_estimate"` + `fxRate`/`fxRateDate`/`fxSource`); la cifra nativa nunca se presenta como MXN.
- Si la consulta de precios falla, el refresco de bundles **falla**: snapshot conservado, `bundles_stale`, timestamp sin avanzar, sin purga.
- Build Your Own solo puede detectarse vía gg.deals (fase 2): hoy no se puede marcar, así que ese caso no está cubierto.

Texto base de la comparación honesta:

> Compara comprar el bundle ahora contra el precio individual **actual** de cada ítem, en el momento de la observación. Los precios pueden cambiar y la disponibilidad no está garantizada.

## E) Fases de implementación (V1)

1. **Verificación de contrato en vivo.** Obtener respuestas reales de `overview/v2` para varios juegos (con y sin bundle). Guardar **fixtures sanitizadas** (sin key, sin datos sensibles), no payloads crudos en el repositorio. **Decision gate:** si el esquema no permite determinar contenido/precio por tier, el cálculo de ahorro se cancela y V1 solo muestra bundles.
2. **Extensión del cliente.** Método nuevo en `IItadClient` / `ItadClient`, reutilizando el mismo `ProviderRequestGovernor` (token bucket compartido) y las mismas reglas de reintento/`Retry-After`. Parseo defensivo: sobre desconocido, campos ausentes o tipos inesperados → se descarta el bundle. Los errores de sobre (`HttpRequestException` / `JsonException`) deben preservar el snapshot persistido, igual que las ofertas.
3. **Modelo y persistencia.** Ver §F. Tabla de snapshot `external_bundles` con clave única acotada por fuente y tiers en JSONB sanitizado. Se actualiza `SQL/schema.sql` (fuente de verdad) **y** una migración fechada idempotente en `SQL/migrations/`. No se guarda el payload crudo del proveedor.
   - **Por qué no `game_offers`:** un bundle puede tener varios tiers y varios ítems, y expira; `game_offers` es un snapshot por `(juego, fuente, oferta)` con un precio. Forzarlo allí rompe el modelo genérico y la purga del comparador.
4. **Integración en el servicio.** Dentro del patrón existente de `SteamGameService` (Read → Outbound → Persistence), con gate por appId igual que las ofertas: misma ventana de refresco o una propia documentada. **Purga acotada por `source`.** Fallo del proveedor → snapshot previo + bandera de stale independiente (añadir campos/contrato si hace falta), **sin** avanzar el timestamp. Sin transacción abierta durante HTTP.
5. **API / BFF / contrato / UI.** Se integra en la **misma ruta de detalle del juego**, sin ruta nueva. Hipervínculo activo a la página del bundle, agrupación por proveedor existente, etiquetas de moneda/FX visibles y **ninguna afirmación verde** sin comparación válida. Sin nombre de vendedor inventado.
6. **Hardening, pruebas y release.** Rate-limit del refresco existente para bundles; validación de TLS, URL, strings y dinero; sin key en logs; builds (`dotnet build Deals.sln`, `pnpm build`) y los casos manuales de §G.

## F) Boceto de esquema (propuesta, no implementación)

Esquema propuesto **pendiente de la validación de contrato** de la fase 1. No es DDL definitivo.

| Columna | Notas |
|---|---|
| `external_bundle_id` | PK |
| `steam_game_id` | FK al juego consultado (por el que aparece el bundle) |
| `source` | `itad` (V1); `ggdeals` en fase 2 |
| `bundle_key` | clave estable dentro de la fuente |
| `provider_bundle_id` | id del proveedor cuando exista |
| `title` | |
| `shop_name` / `shop_id` | |
| `page_url` | página del bundle en la tienda |
| `deal_url` | `url` de ITAD **verbatim**, con tag de afiliado |
| `details` | texto saneado, recortado |
| `published_at` / `expires_at` | timestamps UTC, nulables |
| `counts` | conteos tal como los entregue el proveedor (JSONB) |
| `tiers` | JSONB **sanitizado** con precio/moneda/ítems por tier |
| `observed_at` | UTC; obligatorio |

```text
UNIQUE (steam_game_id, source, bundle_key)
```

Notas:

- `bundle_key` debe ser estable para el upsert. Si la fuente no entrega un id estable (caso de gg.deals fase 2), `bundle_key` se deriva de forma determinista y se documenta.
- Nada de payload crudo: `tiers` y `counts` se normalizan y recortan antes de persistir.
- Sin tablas de "ahorro calculado": el ahorro es derivado y se calcula en lectura, no se almacena como verdad.

## G) Criterios de aceptación y riesgos

Casos que la implementación debe cubrir:

| Caso | Resultado esperado |
|---|---|
| Juego sin bundles | Sin bloque de bundles, sin error, el detalle del comparador intacto |
| Bundle activo | Se muestra con tier, precio, moneda y enlace activo |
| Bundle expirado | No se muestra como activo |
| Contenido incompleto | Se muestra el bundle; **sin cifra de ahorro** y con motivo |
| Build Your Own | Se muestra el bundle; **sin cifra de ahorro** |
| Falta el precio del tier | Sin cifra de ahorro |
| Moneda distinta entre ítems | Sin ahorro, o una conversión FX única claramente etiquetada |
| Fallo del proveedor con snapshot previo | Snapshot conservado, bandera stale, timestamp sin avanzar |
| Purga | Solo borra filas de su propio `source`; nunca toca las ofertas ni el comparador |
| Atribución | Hipervínculo activo a IsThereAnyDeal y `url` sin alterar |
| Secretos | Ninguna key en logs, respuestas, fixtures ni frontend |
| Build | `dotnet build Deals.sln` y `pnpm build` en verde |
| Validación en vivo | Respuesta real de MX con al menos un bundle confirmada antes de cerrar el contrato |

Riesgos:

| Riesgo | Mitigación |
|---|---|
| Sobre de ITAD distinto al esperado | Fase 1 con fixtures reales antes de escribir lógica; descartar lo que no parsee |
| `overview/v2` no devuelve el contenido del tier | Se cancela el cálculo de ahorro; V1 solo muestra bundles (decision gate) |
| Semántica de `addon` ambigua | Sin ahorro para tiers con addon hasta confirmar |
| Moneda sin cobertura FX | Igual que el comparador: se muestra sin convertir y etiquetado |
| Purga que borra ofertas | Filtro obligatorio por `source` |
| Regresión del comparador | Bundles en tabla y bloque propios; prohibido meterlos en `best*` |
| ToS de atribución | Enlace activo y `url` verbatim; requisito contractual |
| Uso comercial de gg.deals fase 2 | Se mantiene como herramienta self-hosted personal; monetizar exige plan Premium negociado |
| Fase 2 sin muestra real con key | No se implementa el parser de gg.deals bundles hasta tenerla |
| Ahorro leído como promesa | Copy obligatorio de comparación actual + sin garantía de stock |
