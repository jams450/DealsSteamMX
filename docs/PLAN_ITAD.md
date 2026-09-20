# DealExt: plan de integración ITAD

Estado: **comparador de precios implementado, commiteado y verificado en producción**. Incluye el cliente y la persistencia de ITAD (fases 1–4): identidad, ofertas por tienda, FX USD→MXN, BFF y UI. El trabajo está en `origin/main` (`865ec95`, `e31eab4`), no staged. La fase 5 (alertas/Telegram) está pendiente y los bundles externos quedan diferidos → `PLAN_BUNDLES.md`.

Este documento refleja el estado real del código, no el plan original. Lo implementado está verificado por lectura cruzada entre entidades, DDL, servicios y consumidores.

Continúa en `PLAN_WISHLIST.md`. Los bundles externos se documentan aparte en `PLAN_BUNDLES.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Decisiones vigentes

| Decisión | Razón |
|---|---|
| Solo tiendas oficiales y autorizadas | ITAD **no cubre keyshops ni grey market**. No existe campo `keyshop` en su API. |
| Solo `type == "game"` | `lookup` devuelve `null` o datos no comparables para DLC, demos, soundtracks y packs. |
| `country=MX`, moneda aceptada como venga | MX tiene 34 tiendas activas y filtra disponibilidad real. ITAD sirve **USD** para MX. |
| Conversión propia USD→MXN | ITAD no entrega MXN. El servicio FX es genérico y reutilizable. |
| ITAD solo en el detalle | La búsqueda usa Steam. ITAD no se consulta al teclear. |
| Gate de 7 días + refresh manual | Presupuesto de llamadas limitado. |
| **Sin webhooks de ITAD** | Solo existe `notification-waitlist`, atado a la waitlist personal del usuario, con OAuth2 + PKCE por usuario y sin firma HMAC. |
| Atribución obligatoria a ITAD | ToS: mencionar a IsThereAnyDeal, no alterar datos, no quitar el tag de afiliado de `url`. |

Shop IDs confirmados en `/service/shops/v1?country=MX`: Steam `61`, GOG `35`, Epic `16`, Ubisoft `62`, Microsoft `48`, EA Store `52`, Blizzard `4`, Humble `37`, Fanatical `6`, GreenManGaming `36`, GameBillet `20`, Nuuvem `50`, WinGameStore `64`.

## 2. Fases completadas

### Fase 1 — cliente ITAD ✅

- `Deals.API/ItadOptions.cs` — `SectionName="ITAD"`; `ApiKey`, `BaseUrl="https://api.isthereanydeal.com/"`, `Country="MX"`, `RefreshAfterDays=7`, `TimeoutSeconds=10`, `OfficialShopIds="61,35,16,62,48,6,36,20,50,64"`.
- `Deals.BusinessLogic/Interfaces/IItadClient.cs`:
  - `Task<string?> LookupSteamAppIdAsync(int appId, CancellationToken ct)`
  - `Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(IReadOnlyCollection<string> itadIds, CancellationToken ct)`
- `Deals.BusinessLogic/Services/ItadClient.cs`:
  - `lookup/id/shop/61/v1` (POST, body `["app/<appId>"]`) — **sin key**, shop id de Steam hardcodeado.
  - `games/prices/v3?country={Country}&shops={shopIds}` (POST, lotes de hasta **200 UUIDs**).
  - **Key por header `ITAD-API-Key`**, nunca en query.
  - `ProviderRequestGovernor` con token bucket 1 req/s y burst 10; retry único en 429 respetando `Retry-After`.
  - Clasificación por **allowlist de configuración** (`IsOfficial = OfficialShopIds.Contains(shopId)`), no por el payload de ITAD.
- `Deals.BusinessLogic/Models/Itad/ItadDeal.cs` — `(ShopId, ShopName, IsOfficial, Currency, RegularPriceMinor, CurrentPriceMinor, DiscountPercent, DealUrl, ObservedAt)`.
- `Deals.BusinessLogic/Models/Itad/ItadGamePrices.cs` — `ItadAmount(AmountMinor, Currency)`, `ItadHistoryLow(All, YearToDate, ThreeMonths)`, `ItadGamePrices(ItadId, HistoryLowes, Deals)`.
- DI en `Deals.API/Extensions/ServiceCollectionExtensions.cs:37-79`: validación en `ValidateOnStart` (HTTPS obligatorio, shop ids numéricos, `ApiKey` no-placeholder) ⇒ **la API no arranca sin `ITAD__ApiKey`**.

### Fase 2 — persistencia e integración al detalle ✅

- `SQL/migrations/2026-09-17_itad_offers.sql` (idempotente) y bloque equivalente en `SQL/schema.sql:50-51, 81-108`:
  - `steam_games.itad_game_id VARCHAR(36)`, `steam_games.offers_refreshed_at TIMESTAMPTZ`.
  - `game_offers` con `UNIQUE (steam_game_id, source, offer_key)`, columnas originales **y** derivadas en MXN, `fx_rate`, `fx_rate_date`, `fx_source`, `classification`, `pricing_type`.
- `Deals.Models/Entities/GameOffer.cs` (`BaseModel`, todas las columnas mapeadas). `AppDbContext`: DbSet, FK con cascade, índice único e índice por `SteamGameId`.
- `SteamGameService.GetByAppIdAsync(int appId, bool forceRefresh, CancellationToken)`:
  - `ConcurrentDictionary<int, SemaphoreSlim>` de gates por appId: serializa DB+HTTP del mismo juego.
  - Steam se consulta **siempre**.
  - `needsOffers = forceRefresh || OffersRefreshedAt is null || antigüedad > RefreshAfterDays` (clamp 1..90).
  - Fallo degradable de ITAD (`HttpRequestException`/`JsonException`/`OperationCanceledException` sin cancelación del cliente) → snapshot persistido + `offersStale=true`, **sin avanzar el timestamp** para reintentar en el siguiente request.
  - Upsert sobre `uq_game_offers`; purga de ofertas obsoletas del juego.
- `Deals.API/Controllers/SteamController.cs`: `GET games/{appId}`, **`POST games/{appId}/refresh`**, más `search` y `suggestions`. Política `UserWithId`.
- `SteamGameResponse` expone `Offers`, `OffersRefreshedAt`, `OffersStale`.

### Fase 3 — conversión USD→MXN ✅

- `SQL/migrations/2026-09-18_fx_rates.sql` (idempotente) + `SQL/schema.sql:111-120`: `fx_rates` con PK `(base, quote, rate_date)`.
- `Deals.API/FxOptions.cs` — `SectionName="FX"`; `BaseUrl="https://www.banxico.org.mx"`, `BaseCurrency="USD"`, `QuoteCurrency="MXN"`, `TimeoutSeconds=10`, `BanxicoToken`.
- `IFxRateService`: `GetRateAsync` (busca hoy, si no hay llama al proveedor) y `GetLatestRateAsync` (**solo lectura, sin fetch, para requests de usuario**).
- `BanxicoFxRateProvider` — serie SIE `SF43718` (FIX), token en header `Bmx-Token`, `dato` parseado de string, solo USD/MXN.
- `FrankfurterFxRateProvider` — `v2/providers/banxico/rate/{base}/{quote}`, sin auth, cualquier par, con lag de ~1 día.
- `Deals.API/HostedServices/FxRateRefreshJob.cs` — `BackgroundService`, delay de arranque 10 s, intervalo 24 h, upsert en `fx_rates`.
- `SteamGameService.ApplyDeal` clasifica `pricing_type`: `MXN → regional`; `USD` con tasa → `fx_estimate` (redondeo `AwayFromZero` con guardas de desborde); cualquier otra moneda → `unconverted`.

### Fase 4 — BFF y UI ✅

- `Deals.Web/app/api/bff/steam/games/[appId]/route.ts` — `GET` reenvía a la API y **`POST` reenvía a `/refresh`**.
- `Deals.Web/app/steam/_lib/steam-api.ts` — `refreshSteamGame(appId)` con `csrfFetch`; `getSteamGame(appId, { refresh })`.
- `Deals.Web/lib/contracts/steam.ts` — `SteamGameOffer` completo, más normalizadores estrictos (`toIsoDate`, `toIsoDateTime`, `toHttpsUrl` con comentario del tag de afiliado).
- `Deals.Web/app/games/[steamAppId]/game-client.tsx` — tabla de ofertas, botón **"Actualizar ofertas"**, badge **"Datos posiblemente desactualizados"**, "Ofertas actualizadas {fecha}", badge "Sin conversión", nota de tasa/fecha/fuente, y atribución `Datos de precios: IsThereAnyDeal`.

## 3. Pendientes derivados de la implementación

Hallazgos de la auditoría. Son trabajo real, no ideas.

### 3.0 Las tiendas oficiales de ITAD (lista verificada)

Lista de tiendas que ITAD marca como **oficiales**, con el volumen que declara cada una. Los números son
el argumento de por qué faltan precios en tiendas que sí venden el juego: `deals` es cuántas ofertas
**de esa tienda** rastrea ITAD en total, no cuántos juegos existen ahí.

| id | Tienda | deals | games | ¿en el allowlist? |
|---|---|---|---|---|
| 2 | AllYouPlay | 766 | 5726 | — |
| 4 | Blizzard | 8 | 789 | — |
| 13 | DLGamer | 753 | 4726 | — |
| 15 | Dreamgame | 1337 | 1948 | — |
| 52 | EA Store | 138 | 597 | — |
| **16** | **Epic Game Store** | 1146 | 14972 | **sí** (y constante de identidad) |
| **6** | **Fanatical** | 10088 | 16075 | **sí** |
| 17 | FireFlower | 0 | 253 | — |
| 75 | Fortuna Digital | 367 | 450 | — |
| **20** | **GameBillet** | 6618 | 7047 | **sí** |
| 24 | GamersGate | 1978 | 10326 | — |
| 25 | Gamesload | 719 | 1663 | — |
| 27 | GamesPlanet DE | 7124 | 7978 | — |
| 28 | GamesPlanet FR | 7150 | 8010 | — |
| 26 | GamesPlanet UK | 7111 | 8003 | — |
| 29 | GamesPlanet US | 7625 | 8519 | — |
| **35** | **GOG** | 7301 | 12503 | **sí** |
| **36** | **GreenManGaming** | 3970 | 12599 | **sí** |
| 37 | Humble Store | 1654 | 13270 | — |
| 42 | IndieGala Store | 1456 | 8800 | — |
| 65 | JoyBuggy | 0 | 0 | — |
| 47 | MacGameStore | 672 | 4767 | — |
| **48** | **Microsoft Store** | **407** | 6279 | **sí** (y constante de identidad) |
| 77 | Muve | 0 | 0 | — |
| 49 | Newegg | 653 | 4807 | — |
| **50** | **Nuuvem** | 594 | 3060 | **sí** |
| 73 | PlanetPlay | 507 | 4498 | — |
| 74 | PlayerLand | 1542 | 2038 | — |
| 70 | Playsum | 1387 | 3864 | — |
| **61** | **Steam** | 37441 | 310373 | **sí** (y constante de identidad) |
| **62** | **Ubisoft Store** | 410 | 784 | **sí** (y constante de identidad) |
| **64** | **WinGameStore** | 3016 | 6600 | **sí** |
| 78 | Zapagames | 346 | 1782 | — |
| 72 | ZOOM Platform | 0 | 938 | — |

Lectura de los números, que es lo que importa:

- **Microsoft Store tiene 407 ofertas en total.** Con esa cobertura, que un juego que la tienda vende a MXN
  249 no aparezca es lo normal, no la excepción: medido con `Graveyard Keeper` (appid 599140), ITAD no tiene
  deal de la shop `48` **en ningún país** (probado US, DE, GB y BR) y el buscador de la tienda tampoco lo
  indexa. Sin enlace que seguir y sin resultado que aceptar, no hay precio de Microsoft. El límite es del
  proveedor, no del código.
- La allowlist actual (`61,35,16,62,48,6,36,20,50,64`) son **10 de las 34** oficiales y todas están en esta
  lista ✓. Las otras 24 no llegan nunca porque `shops=` las excluye de la petición: no se clasifican mal, no
  se piden. Ampliarla da más ofertas por juego y no toca ninguna decisión de identidad.
- Tiendas con `deals: 0` (FireFlower, JoyBuggy, Muve, ZOOM): están listadas pero no rastrean nada. Meterlas
  en el allowlist no cambiaría ninguna respuesta.

### 3.1 La clasificación `authorized` — **cerrada: era media verdad**

Estado: **decidido y aplicado**. La medición en código corrigió el diagnóstico que este documento tenía
escrito, y por eso la decisión no es la A ni la B que figuraban abajo.

Lo que decía el documento: «la rama `IsOfficial=false → "authorized"` no puede alcanzarse nunca». Es cierto
para ITAD y **falso para la constante**: el agregado de gg.deals la usa a propósito
(`SteamGameService.ApplyGgDealsPrice`), porque ese bucket suma tiendas oficiales y autorizadas sin decir cuál y
etiquetarlo `official` sería mentir, así que `authorized` es su única etiqueta honesta. La UI no le pinta badge.

Lo que se cerró, entonces, es la parte que sí estaba muerta:

| Pieza | Acción |
|---|---|
| `ItadDeal.IsOfficial` | **Eliminado.** Era el resultado de `OfficialShopIds.Contains(shopId)`, calculado en cada oferta para alimentar un ternario que siempre daba lo mismo |
| El ternario de `ApplyItadDeal` | **Eliminado**: `offer.Classification = OfficialClassification;` con el porqué en el comentario |
| `AuthorizedClassification` | **Se queda.** Vivo y correcto en el agregado de gg.deals |
| El parámetro `shops=` | **Se queda.** Es lo que hace cierta la afirmación: se piden solo tiendas oficiales, así que toda oferta que llega es oficial |

Invariante que ahora vive en el comentario del código, junto a la constante: **la lista que se pide a ITAD y la
lista de tiendas oficiales son la misma.** Meter en `ITAD__OfficialShopIds` una tienda que no sea oficial la
etiqueta como oficial — el código ya no puede distinguirlo, y no debe: la configuración es la afirmación. Si
algún día se piden tiendas no oficiales, hay que volver a decidirlo en `ApplyItadDeal`.

Reabrir el caso «B» (traer todas las tiendas de ITAD y clasificar de verdad) sigue costando lo mismo que antes:
quitar `shops=`, y aquí ya no hay nada que restaurar en la ruta de ITAD porque la etiqueta correcta para una
tienda no oficial sería `authorized`, que sigue existiendo.

### 3.2 `historyLow` se parsea y no se usa

`ItadGamePrices.HistoryLowes` se llena en cada llamada (`all`, `y1`, `m3`) y **no se consume en ningún lado**: no se persiste ni se muestra. Viene gratis dentro de `prices/v3`.

Recomendación: **persistirlo**, no borrarlo. Es la base para el tipo de alerta `history_low` de la fase 5 y para mostrar "mínimo histórico" en el detalle. Requiere columnas nuevas en `game_offers` (`history_low_all_minor`, `history_low_currency`) en una migración fechada. Si en la fase 5 se decide alertar solo por precio objetivo, entonces sí conviene eliminar el parseo.

### 3.3 FX tiene cobertura limitada por diseño

Solo hay tasa USD→MXN. Cualquier otra moneda queda `unconverted`. Es aceptable: Banxico solo publica el FIX peso/dólar y Frankfurter cubre el resto pero con lag de un día.

Si aparece una oferta en EUR o BRL, se muestra sin convertir y con el badge correspondiente. Ampliar el par exige decidir si se cambia el proveedor primario a Frankfurter.

### 3.4 `FxRateRefreshJob` no está alineado con Banxico

El intervalo corre **desde el arranque** y sin jitter. Banxico publica el FIX ~12:00 CDMX en días hábiles. En un arranque desfasado, el job puede correr sistemáticamente antes de la publicación y quedarse con el valor del día anterior.

Mejora pequeña: alinear la primera corrida a después de las 18:30 UTC y añadir una segunda oportunidad si la tasa obtenida no es de hoy. Un fallo diario hoy solo se reintenta 24 h después.

### 3.5 Fail-fast de despliegue

`ValidateOnStart` hace que un `ITAD__ApiKey` o `FX__BanxicoToken` con placeholder **rompa el arranque**. Es deseable, pero hay que reflejarlo en el orden de despliegue: primero la variable en el entorno, después el contenedor.

## 4. Fase 5 — alertas y Telegram (pendiente)

Sin una línea de código escrita. Diseño completo en `PLAN_WISHLIST.md` §7, que la absorbe como parte del flujo de wishlist.

Resumen:

- `Deals.API/TelegramOptions.cs` — **simplificado** respecto a `Gastos`: solo `Enabled`, `BotToken`, `ChatId`. DealExt solo envía, no recibe comandos, así que no hacen falta `AllowedUserId` ni `AppUserId`.
- `Deals.API/Extensions/TelegramConfigurationExtensions.cs` — bind + `ValidateOnStart` condicionado a `Enabled` + guard de placeholders `SET_`.
- `TelegramBotClientProvider` copiado de `GastosApp.API/Services/Telegram/TelegramBotClientProvider.cs`: cliente lazy, truncado a 4000 caracteres, **nunca loguea el token** (solo `exception.GetType().Name`).
- `TelegramWebhookController.cs` → **no se copia**. No se exponen webhooks.
- `Deals.API/HostedServices/PriceAlertScheduler.cs`, junto a `FxRateRefreshJob`.
- Tabla `price_alerts` con `max_price_type` (`current` | `history_low`).

## 5. Orden de despliegue

1. Aplicar las tres migraciones si aún no están: `2026-09-16_steam_games_image_url`, `2026-09-16_steam_games_lowest_price`, `2026-09-17_itad_offers`, `2026-09-18_fx_rates`.
2. Cargar `ITAD__ApiKey` y `FX__BanxicoToken` en el entorno **antes** de recrear el contenedor `api` (fail-fast).
3. Recrear `api`.

**Estado (verificado en `HEAD`):** el trabajo de ITAD y FX ya está commiteado (`05fff68`, `80b7e5f`),
incluidas `2026-09-17_itad_offers.sql` y `2026-09-18_fx_rates.sql`. El aviso anterior de "staged y sin
commitear" quedó obsoleto.

## 6. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un appid mapea a un pack o familia distinta | Persistir `itad_game_id`, comparar título, marcar como no comparable en vez de unir en silencio |
| `lookup` devuelve `null` (delisted, DLC, demo) | Limitar a `type == "game"` y mostrar estado explícito |
| Un bug dispara N llamadas por render | Gate de 7 días + `ProviderRequestGovernor` + gates por appId + `Retry-After` |
| Moneda distinta a MXN presentada como regional | Columna de moneda siempre visible; el convertido va marcado como aproximado con tasa y fecha |
| ITAD cambia o revoca acceso | Degradación a snapshot persistido + `offersStale`; el detalle nunca queda vacío |
| Divergencia de índices entre `AppDbContext` y `schema.sql` | Revisar al añadir la migración de `historyLow` |

## 7. Fuera de alcance

- Keyshops y grey market (ITAD no los cubre; requiere gg.deals u otro proveedor).
- OAuth2 con ITAD y webhooks `notification-waitlist`.
- Bundles externos: `prices/v3` no los expone, así que no son una carencia del comparador de precios. Es una fase propia y separada → `PLAN_BUNDLES.md`.
- Conversión FX histórica.
- Divisas distintas a USD→MXN.
