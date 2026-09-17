# DealExt: plan de integración gg.deals

Estado: **implementado** (fases 1–4). Contrato externo verificado contra la documentación oficial y confirmado con una API key real (`region=us` devuelve USD). `dotnet build Deals.sln` y `pnpm build` en verde. Falta aplicar la migración y desplegar con la variable de entorno.

Alcance: obtener el precio agregado de tiendas oficiales y de keyshops por Steam AppID mediante la API de gg.deals, convertirlo a MXN con el servicio FX existente y mostrarlo en el detalle del juego.

Continúa a `PLAN_ITAD.md`, `PLAN_WISHLIST.md` y `PLAN_TELEGRAM.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Decisiones tomadas

| Decisión | Razón |
|---|---|
| Precios separados retail vs keyshop | La API devuelve `prices.currentRetail` y `prices.currentKeyshops`. La distinción ya viene hecha. |
| Precio como string con dos decimales | Los valores llegan como strings (`"91.99"`), o `null` por campo. No hay campo de unidades menores. Hay que parsear a decimal y pasar a `int` minor (×100). |
| **Soporte MXN: no hay región `mx`** | Las 19 regiones documentadas son `au, be, br, ca, ch, de, dk, es, eu, fi, fr, gb, ie, it, nl, no, pl, se, us`. `us` es la región por defecto. Se solicita `us` y se convierte con el servicio FX. |
| API key en query param | Es la única forma soportada. `X-API-Key` y `Authorization: Bearer` devuelven `400 Missing required parameters: key`. El patrón de header de `ItadClient` **no** se puede copiar aquí. |
| Rate limit: 100 registros/minuto y 1000/hora | Cada ID cuenta como un registro. En v1 el detalle pide **un** appId por request, así que el governor (1 req/s ⇒ 60 registros/min) queda por debajo del tope. Los límites solo aprietan con un job masivo, que es de `PLAN_WISHLIST.md`. |
| Caché de 7 días | Igual que ITAD. |
| Atribución obligatoria | ToS: crédito a GG.deals **con hipervínculo activo en cada página** donde se muestren los datos, y no alterar ni eliminar los enlaces de afiliado. |
| Sin webhooks ni notificaciones push | No documentados. |
| **Solo agregado, sin identidad de keyshop** | El tier gratuito no devuelve qué vendedor ofrece el precio. Coincide con `PLAN_BASE_MVP.md` §5: no inventar nombres de vendedores. |
| Uso personal/hobby/open‑source permitido | Uso comercial **no** permitido en el tier gratuito; requiere plan Premium negociado. |
| **Persistencia en `game_offers`** | La tabla ya es *"provider-neutral current offer snapshot per (game, source, offer_key)"* (`SQL/schema.sql:78-80`). No se añaden columnas de precio; ver §5. |
| **Ventana de refresco compartida con ITAD** | Sin knob propio: se reutiliza `SteamOffersSettings.RefreshAfterDays`, que hoy se alimenta de `ITAD:RefreshAfterDays`. |
| **Governor único y genérico** | `ItadRequestGovernor` se renombra a un nombre neutro y sigue siendo un singleton compartido. Un solo presupuesto para todos los proveedores. |
| **Mínimos históricos persistidos** | Mismo tratamiento que ITAD: columnas genéricas en `game_offers` (§5). Cierra el pendiente `PLAN_ITAD.md` §3.2. |
| **Secciones por proveedor, no por tipo de tienda** | La UI agrupa por ITAD y gg.deals, no por "oficial" vs "keyshop". Ver §7. |
| `DESIGN.md` se actualiza | §11 prohíbe keyshops; se cambia la política porque gg.deals entra en alcance. Ver §7. |

## 2. Contrato externo verificado

```http
GET https://api.gg.deals/v1/prices/by-steam-app-id/?ids=1,420&key=<key>&region=us
```

Respuesta:

```json
{
  "success": true,
  "data": {
    "1": null,
    "420": {
      "title": "Half-Life 2: Episode Two",
      "url": "https://gg.deals/game/half-life-2-episode-two/",
      "prices": {
        "currentRetail": "91.99",
        "currentKeyshops": "24.30",
        "historicalRetail": "2.89",
        "historicalKeyshops": "5.61",
        "currency": "PLN"
      }
    }
  }
}
```

Detalles que condicionan el parser:

- El sobre es `{ success, data }`. Las claves de `data` son **strings** con el app id.
- `data["<appId>"] === null` significa juego no encontrado; es distinto de un campo `null` dentro del objeto.
- Precios: string con dos decimales, o `null` por campo cuando no hay precio.
- **Los campos de keyshop van en plural**: `currentKeyshops` y `historicalKeyshops`. Escribirlos en singular no falla: devuelve `null` en silencio y el gate de 7 días no lo detecta. `currentRetail` y `historicalRetail` son singulares.
- `currency` está **dentro de `prices`**, no al nivel de `title`/`url`.
- `historical*` son mínimos históricos, no una serie temporal: la API no expone historial navegable.
- Límites: `x-ratelimit-limit`, `x-ratelimit-remaining`, `x-ratelimit-reset`. `Retry-After` **no** está documentado, así que el patrón de reintento de `ItadClient` no se traslada tal cual.

## 3. Dependencias existentes que se reutilizan

- `IFxRateService.GetLatestRateAsync(string baseCurrency, string quoteCurrency, CancellationToken)` — solo lectura, sin fetch. Son **tres** argumentos.
- `Deals.API/HostedServices/FxRateRefreshJob.cs` — mantiene la tasa del día en `fx_rates`.
- `ItadRequestGovernor` (`Deals.BusinessLogic/Services/ItadClient.cs:17-44`) — token bucket 1 req/s, burst 10, `QueueLimit = 0` (lanza `HttpRequestException` al agotarse). **Se renombra a un nombre genérico** y se comparte.
- `SteamGameService.GetByAppIdAsync(int appId, bool forceRefresh, CancellationToken)` — punto de extensión, con gate por appId, ventana de refresco y degradación a snapshot.
- `SteamGameService.ApplyDeal` + `ConvertMinor` — clasifican `pricing_type` y convierten a MXN con guardas de desborde.
- `POST /api/steam/games/{appId}/refresh` (`SteamController.cs:45`) y su reenvío por el BFF (`app/api/bff/steam/games/[appId]/route.ts:65`). El refresco es un **POST a una ruta**, no un query param.
- En la UI, `offerMxnCell` (`game-client.tsx:51`) ya renderiza `≈`, la nota `Tasa · fecha · fuente` y el estado sin conversión para cualquier fila con forma de oferta.

Nota: `PLAN_ITAD.md` describe ITAD y FX como "implementadas sin commitear"; ya están commiteadas. Ese header está desactualizado.

## 4. Fase 1: cliente gg.deals

Archivos nuevos:

- `Deals.API/GgDealsOptions.cs` — `SectionName = "GgDeals"`; `ApiKey`, `BaseUrl` (default `https://api.gg.deals/v1/`), `Region` (default `us`), `TimeoutSeconds`. **Sin `RefreshAfterDays`**: la ventana la aporta `SteamOffersSettings`.
- `Deals.BusinessLogic/Interfaces/IGgDealsClient.cs`:
  - `Task<IReadOnlyDictionary<int, GgDealsGamePrice>> GetPricesAsync(IReadOnlyCollection<int> steamAppIds, CancellationToken ct)`
- `Deals.BusinessLogic/Models/GgDeals/GgDealsGamePrice.cs` — `SteamAppId`, `Title`, `Url`, `CurrentRetailMinor`, `CurrentKeyshopsMinor`, `HistoricalRetailMinor`, `HistoricalKeyshopsMinor`, `Currency`.
- `Deals.BusinessLogic/Services/GgDealsClient.cs`:
  - `GET {BaseUrl}prices/by-steam-app-id/?ids=<csv>&key=<ApiKey>&region=<Region>`.
  - Parseo del sobre `success`/`data`, claves string, `null` por juego y por campo.
  - Conversión de string a `int` minor: `decimal.TryParse` con `CultureInfo.InvariantCulture`, redondeo `AwayFromZero` antes del cast, `null` si falla. Guarda también para el literal `"null"` (no documentado, pero barato).
  - Chunking: la firma acepta colección, pero en v1 el detalle pide **un** appId. No implementar el troceado de 100 hasta que exista el consumidor masivo de `PLAN_WISHLIST.md`.
  - Governor: usa el singleton renombrado. El `try/catch` debe ser **separado** del de ITAD, porque el bucket compartido puede lanzar `HttpRequestException` y no debe tumbar la otra fuente.
  - Reintento en 429 leyendo `x-ratelimit-reset`. Sin `Retry-After`.
  - Devuelve diccionario solo con los IDs que tienen datos.
- Rename del governor: `ItadRequestGovernor` → nombre neutro (p. ej. `ProviderRequestGovernor`). Toca `ItadClient.cs:17,46` y `ServiceCollectionExtensions.cs:66`. Es mecánico, pero sale de la carpeta gg.deals y conviene hacerlo como cambio aislado antes de la Fase 1.
- DI en `Deals.API/Extensions/ServiceCollectionExtensions.cs`: `AddHttpClient<IGgDealsClient, GgDealsClient>`, siguiendo el patrón exacto de `ItadOptions` (`L37-50`): HTTPS obligatorio, `ApiKey` no vacío y sin prefijo `SET_`, con `ValidateOnStart`.
- Bind de la sección `GgDeals` en `Program.cs` (junto a `Steam`, `ITAD` y `FX`).
- Configuración y despliegue:
  - `docker-compose.yml` debe pasar `GgDeals__*` (hoy pasa `ITAD__*` y `FX__*` en `L38-48`).
  - `.env.example` debe declarar las claves con placeholder.
  - Por el `ValidateOnStart`, la API **no arranca** sin la variable: el orden es entorno primero, contenedor después.

## 5. Fase 2: persistencia e integración al detalle

### DDL

```sql
-- SQL/migrations/2026-09-XX_ggdeals_offers.sql (idempotente) + bloque en SQL/schema.sql
ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS ggdeals_refreshed_at TIMESTAMPTZ;

ALTER TABLE public.game_offers
    ADD COLUMN IF NOT EXISTS history_low_all_minor INT,
    ADD COLUMN IF NOT EXISTS history_low_currency VARCHAR(3);

ANALYZE public.steam_games;
ANALYZE public.game_offers;
```

No hacen falta columnas de precio, moneda ni timestamp por oferta: `game_offers` ya las tiene. No hace falta índice nuevo: las consultas van por `steam_game_id`, ya indexado, y por la clave única.

`history_low_all_minor` / `history_low_currency` son **genéricas**, no de gg.deals: las usa también ITAD con `historyLow.all`. Esto cierra el pendiente `PLAN_ITAD.md` §3.2, que ya recomendaba exactamente esas dos columnas.

### Mapeo a `game_offers`

Dos filas por juego, con la forma que el modelo genérico ya define:

| Columna | Retail | Keyshop |
|---|---|---|
| `source` | `"ggdeals"` | `"ggdeals"` |
| `offer_key` | `"retail"` | `"keyshop"` |
| `shop_id` | `null` | `null` |
| `shop_name` | `"GG.deals"` | `"GG.deals keyshops"` |
| `classification` | `"official"` | `"keyshop"` |
| `original_currency` | `prices.currency` | `prices.currency` |
| `original_regular_price_minor` | `null` | `null` |
| `original_current_price_minor` | `currentRetail` | `currentKeyshops` |
| `history_low_all_minor` | `historicalRetail` | `historicalKeyshops` |
| `history_low_currency` | `prices.currency` | `prices.currency` |
| `pricing_type` | vía `ApplyDeal` | vía `ApplyDeal` |
| `discount_percent` | `null` | `null` |
| `deal_url` | `url` del API | `url` del API |
| `mxn_*`, `fx_*` | vía `ApplyDeal` | vía `ApplyDeal` |
| `drm_names`, `platform_names` | `[]` | `[]` |

Dos consecuencias a tener presentes:

- gg.deals no entrega precio base ni porcentaje de descuento, así que `original_regular_price_minor` y `discount_percent` van a `null`. Hay que verificar cómo los renderizan `offerPriceMinor`, `selectBestPrice` y `formatComparablePrice` antes de dar la UI por buena.
- `deal_url` se guarda con el `url` que devuelve la API y se renderiza verbatim, sin alterarlo: la ToS lo exige. `toHttpsUrl` (`steam.ts:142-149`) ya lo hace así a propósito.

### Servicio y endpoint

- `Deals.BusinessLogic/Services/SteamGameService.cs` — `GetByAppIdAsync` se extiende con un gate paralelo al de ITAD, sobre la misma ventana:
  ```
  needsGgDeals = forceRefresh
              || game.GgDealsRefreshedAt is null
              || game.GgDealsRefreshedAt < refreshWindowStart
  ```
  - `try/catch` **separado** del de ITAD, con la misma clasificación de fallos degradables.
  - Fallo → se conserva el snapshot previo, **no se avanza el timestamp** y se marca `GgDealsStale = true` para reintentar en el siguiente request.
  - **Juego no encontrado** (`data["<appId>"] === null`) no es un fallo: se guarda igualmente `ggdeals_refreshed_at` y se deja sin ofertas, replicando el camino de "no comparable" de ITAD (`SteamGameService.cs:232-241`). Sin esto se reintenta en cada request para siempre.
  - Upsert por `uq_game_offers` con `(Source, OfferKey)`.
  - **La purga de ofertas obsoletas (`SteamGameService.cs:371-379`) debe filtrar por `source`.** Hoy borra todo lo que no venga en la respuesta; si gg.deals comparte la tabla y el filtro no se limita a su propio `source`, un fallo de un proveedor borra las filas del otro.
  - Poblar también `history_low_*` para las ofertas ITAD desde `historyLow.all`, que hoy se descarta.
- `Deals.API/Controllers/SteamController.cs` — sin cambios: `POST games/{appId}/refresh` ya fuerza el refresco de todo el detalle.
- `Deals.API/Models/Steam/SteamGameResponse.cs` — añadir `GgDealsRefreshedAt` y `GgDealsStale`. Los precios **no** se añaden a la respuesta: ya viajan dentro de `Offers`. Añadir también `HistoryLowAllMinor` y `HistoryLowCurrency` a `SteamGameOfferResponse`.
- `Deals.Models/Entities/SteamGame.cs` — añadir `GgDealsRefreshedAt` con su `[Column]`, y mapearlo en `AppDbContext` como el resto.

Orden de despliegue: variable de entorno, DDL, aplicación.

## 6. Fase 3: conversión a MXN

No requiere lógica nueva. Al persistir en `game_offers`, la conversión la hace el `ApplyDeal` existente:

1. `pricing_type` se resuelve igual que para ITAD: `regional` si la moneda es MXN, `fx_estimate` si es USD y hay tasa, `unconverted` en cualquier otro caso.
2. La tasa se lee con `IFxRateService.GetLatestRateAsync`, nunca con `GetRateAsync`, para no disparar fetch desde un request de usuario.
3. El redondeo `AwayFromZero` y las guardas de desborde ya están en `ConvertMinor`.

No hacen falta "helpers de conversión y formato" en el frontend: `offerMxnCell` ya los cubre.

## 7. Fase 4: BFF y UI

BFF: sin cambios. `POST /api/bff/steam/games/[appId]` ya reenvía al endpoint de refresco.

Contratos (`Deals.Web/lib/contracts/steam.ts`):

- Añadir `"keyshop"` a `STEAM_OFFER_CLASSIFICATIONS` (`L10`) **y a `toClassification` (`L85-88`)**, que hoy hardcodea `official|authorized`. Es un fallo silencioso: `normalizeOffer` (`L188-196`) devuelve `null` cuando la clasificación no valida, así que las filas keyshop desaparecerían sin error visible.
- Añadir `historyLowAllMinor` / `historyLowCurrency` a `SteamGameOffer`.
- Añadir `ggDealsRefreshedAt` y `ggDealsStale` a `SteamGame`, siguiendo el patrón de `offersStale` (`L249`).

UI (`Deals.Web/app/games/[steamAppId]/game-client.tsx`):

- **Agrupar por proveedor, no por tipo de tienda.** Hoy hay un único grupo, `OfferGroup` con `OFFICIAL_GROUP_HEADING = "Tiendas oficiales"` (`L17`), y el filtro `classification === "official"` (`L365`). Pasa a dos grupos:
  - Grupo **ITAD** — id `offers-itad`, las ofertas `source === "itad"` (tiendas oficiales).
  - Grupo **gg.deals** — id `offers-ggdeals`, las ofertas `source === "ggdeals"` (retail y keyshop).
- El encabezado deja de ser "Tiendas oficiales" y pasa a nombrar el proveedor. La columna Fuente ya muestra `shopName`, que distingue `GG.deals` de `GG.deals keyshops`.
- Reutilizar `OfferGroup` y `offerMxnCell` tal cual. No crear helpers de formato nuevos.
- `cheapestTies` / `comparableOffers` / `selectBestPrice` (`L91-123`) hoy comparan dentro del grupo oficial. Con dos grupos hay que decidir si el "mejor precio comparable" es por grupo o global, y **una estimación FX de keyshop no debe competir contra precios regionales**: `DESIGN.md` §4 exige que la base de comparación sea explícita y siempre visible.
- Badge **"Datos posiblemente desactualizados"** cuando `offersStale` **o** `ggDealsStale` estén activos.
- Atribución: **hipervínculo activo**, no texto. `DESIGN.md` §8 (`L296-300`) ya impone `target="_blank" rel="noopener noreferrer"` y el `sr-only` de "se abre en una pestaña nueva". Hoy `ITAD_ATTRIBUTION` (`L18`) es una constante de texto y habrá que convertirla en algo por proveedor.
- Revisar el copy del botón "Actualizar ofertas" (`L547`): ahora refresca ambos proveedores.
- Los badges son clases CSS en `app/globals.css`; no hay componente `Badge` ni `Stat`, y `Alert` solo tiene `danger|info`. Si keyshop necesita un token visual nuevo, hay que declararlo primero en `DESIGN.md` (regla "reuse before extend", `L67`).
- **`DESIGN.md`**: actualizar §11 (`L368-373`), que hoy prohíbe keyshops, la palabra "keys" y cualquier afirmación de "todas las tiendas". También la tabla de semántica de ofertas (§4) si keyshop necesita fila propia, y la sección de estructura de página para reflejar el agrupado por proveedor.

## 8. Decisiones

### Resueltas e implementadas

1. **`DESIGN.md` §11 reescrito.** gg.deals entra como segundo proveedor de primera clase y su agregado de keyshop está deliberadamente en alcance; la UI los etiqueta "Keyshop". Sigue prohibido raspar, inventar o adivinar el nombre de un vendedor, y afirmar "todas las tiendas".
2. **Ventana de refresco compartida con ITAD.** Sin knob propio: se reutiliza `SteamOffersSettings.RefreshAfterDays` (`ITAD:RefreshAfterDays`).
3. **Governor único y genérico.** `ItadRequestGovernor` → `ProviderRequestGovernor`, singleton compartido. El rename obligó a corregir `PLAN_ITAD.md` y `PLAN_WISHLIST.md`, que citaban el nombre viejo.
4. **Mínimos históricos persistidos** en `game_offers.history_low_all_minor` / `history_low_currency`, genéricas y alimentadas por ambos proveedores. Cierra `PLAN_ITAD.md` §3.2.
5. **UI agrupada por proveedor**: grupos `ITAD` y `gg.deals`, con el "mejor precio comparable" calculado por grupo y una estimación FX que nunca gana en verde sobre un precio regional.
6. **Región `us` → USD** confirmado con una llamada real.
7. **gg.deals no encola alertas en v1.** Sin scheduler ni enqueuer; el dispatcher de `PLAN_TELEGRAM.md` es genérico y aceptará la fuente cuando exista.
8. **La mitigación de la key en logs no vive en `appsettings.json`**, que está en `.gitignore` y no se versiona, sino en `Program.cs`: filtro de `System.Net.Http.HttpClient` a `Warning`.

### Pendientes

1. **`classification = "authorized"` sigue inalcanzable** (`PLAN_ITAD.md` §3.1). El enum la conserva y `DESIGN.md` documenta que ningún proveedor la devuelve y la UI no la pinta. Resolver en un cambio aparte, borrando la rama muerta, o quitando `shops=` de la consulta ITAD para activarla de verdad.
2. **Los mínimos históricos llegan normalizados al frontend pero no se renderizan.** Deuda declarada en `DESIGN.md`.
3. **`ITAD:RefreshAfterDays` gobierna dos proveedores.** El nombre de la sección quedó engañoso; moverlo a una sección neutra si molesta.
4. **`ItadHistoryLow` trae `yearToDate` y `threeMonths`** que se siguen parseando y descartando.
5. **No hay aserciones versionadas.** El repo no tiene runner de pruebas; la verificación fue build más un guion transitorio en `/tmp`. La trampa del plural en `currentKeyshops` y el descarte silencioso en `toClassification` son los dos candidatos obvios a prueba permanente.
6. **`PLAN_TELEGRAM.md` §5 fija la atribución como texto de ITAD.** Con un segundo proveedor debe resolverse por `source`.
7. **`AGENTS.md` sigue describiendo `privateRoutes`** en `middleware.ts`, que no existe: el gate real es `lib/security/route-policy.ts#isPublicRoute`.

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| **Nombres de campo en plural** (`currentKeyshops`) | Verificado contra la documentación. Escribirlos en singular devuelve `null` sin error: cubrir con un test de parseo sobre la respuesta de ejemplo. |
| **`toClassification` descarta keyshop en silencio** | Actualizar el enum y el validador a la vez que el backend. Es el punto más fácil de romper sin que falle ningún build. |
| **Región `us` no da precios reales para MX** | No es un riesgo sino un hecho: `mx` no existe. Se usa `us` como mejor aproximado. |
| API key en query param → filtración en logs | `"System.Net.Http.HttpClient": "Warning"` en las settings de logging y nunca incluir la URL completa en mensajes de error. |
| Governor compartido lanza excepción al agotarse | `try/catch` separado por proveedor y degradación independiente. |
| **Purga por `source` mal acotada** | Filtrar el borrado de ofertas obsoletas por `source`, o un fallo de un proveedor borra las del otro. |
| **Reintento infinito si el juego no está en gg.deals** | Avanzar `ggdeals_refreshed_at` cuando la respuesta es `null` para el appId. |
| Keyshop compitiendo con precios regionales | La estimación FX no entra en el "mejor precio comparable"; base de comparación siempre visible (`DESIGN.md` §4). |
| gg.deals cambia o revoca acceso | Degradación a snapshot persistido + `ggDealsStale`; el detalle nunca queda vacío. |
| Precio como string con formato inesperado | `decimal.TryParse` con `CultureInfo.InvariantCulture`; fallback a `null`. |
| **Violación de la ToS por atribución incompleta** | Hipervínculo activo y `url` del API sin alterar. Es requisito contractual, no cosmético. |
| Uso comercial no permitido | DealExt se mantiene como herramienta self‑hosted de un admin. Monetizarlo exige plan Premium. |

## 10. Fuera de alcance

- Historial de precios navegable: la API no expone serie temporal.
- Bundles vía `/bundles/by-steam-app-id` (fase futura).
- Webhooks y notificaciones push.
- Identidad por vendedor de keyshop: es función del plan Premium.
- Conversión a otras monedas además de USD→MXN.
- Scheduler de alertas y enqueuer para Telegram (ver §8, pendiente 2).

## 11. Verificación

| Fase | Evidencia |
|---|---|
| 1 | Llamada real con una API key válida y datos correctos para el AppID `3375780`, confirmando que `region=us` devuelve `"USD"`. Test de parseo sobre la respuesta de ejemplo de §2, incluidos `data["1"] = null` y los campos en plural. |
| 2 | `dotnet build Deals.sln`; detalle con y sin gg.deals disponible; juego no encontrado sin bucle de reintento; gate de frescura compartido; `POST /refresh`; dos filas en `game_offers` con `source = 'ggdeals'` y `history_low_*` poblado en las de ITAD; purga acotada por `source`; DDL y variable de entorno aplicadas antes del deploy. |
| 3 | Fila del día en `fx_rates`; las ofertas de gg.deals quedan `fx_estimate` con tasa, o `unconverted` sin ella. |
| 4 | `pnpm build` en `Deals.Web`; dos grupos por proveedor visibles, precio original, aproximado en MXN, badge de stale con cualquiera de los dos timestamps, y atribución como hipervínculo activo a gg.deals. |

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```
