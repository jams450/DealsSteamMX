# DealExt: plan de integración ITAD

Estado: propuesto, pendiente de aprobación.

Alcance de este documento: implementar el cliente ITAD, integrarlo al detalle de juego existente, agregar el servicio de conversión USD→MXN y las notificaciones por Telegram.

Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Decisiones tomadas

Apoyadas en verificación en vivo contra la API y las docs oficiales.

| Decisión | Razón |
|---|---|
| Solo tiendas oficiales y autorizadas | ITAD **no cubre keyshops ni grey market**. No existe campo `keyshop` en su API. La columna "keys" requiere otro proveedor (gg.deals), fuera de este alcance. |
| Solo `type == "game"` | `lookup` devuelve `null` o datos no comparables para DLC, demos, soundtracks y packs. |
| `country=MX` pero moneda aceptada como venga | MX tiene 34 tiendas activas y filtra disponibilidad real. ITAD sirve **USD** para MX, no MXN confirmado. |
| Conversión propia USD→MXN | ITAD no entrega MXN. El servicio FX es genérico para reutilizarlo con gg.deals después. |
| ITAD solo en el detalle, nunca en la búsqueda | La búsqueda ya usa Steam. ITAD no se consulta al teclear. |
| Gate de 7 días + refresh manual | Presupuesto de llamadas limitado. 1 llamada keyed por juego cada 7 días. |
| **Sin webhooks de ITAD** | Solo existe el evento `notification-waitlist`, atado a la waitlist personal del usuario, con OAuth2 + PKCE por usuario y sin firma HMAC. Ver §7. |
| Telegram desde scheduler propio | Reutiliza el patrón de `Gastos` sin OAuth con ITAD ni endpoint público expuesto. |
| Atribución obligatoria a ITAD | ToS: mencionar a IsThereAnyDeal, no alterar datos, no quitar el tag de afiliado de `url`. |

### Endpoints oficiales que se usan

| Uso | Endpoint | Key |
|---|---|---|
| AppID → ITAD ID | `POST https://api.isthereanydeal.com/lookup/id/shop/61/v1`, body `["app/<appid>"]` | **No** |
| Precios por tienda | `POST https://api.isthereanydeal.com/games/prices/v3?country=MX&shops=<ids>`, body `["<uuid>", ...]` (máx. 200) | Sí, header `ITAD-API-Key` |

- Shop IDs: Steam `61`, GOG `35`, Epic `16`, Ubisoft `62`, Microsoft `48`, Fanatical `6`, GreenManGaming `36`, GameBillet `20`, Nuuvem `50`, WinGameStore `64`.
- Usar `amountInt` (entero ×100), nunca `amount` (float).
- `historyLow` viene incluido en `prices/v3`; no llamar `historylow/v1` por separado.
- Rate limit: 1000 req / 5 min con email verificado. Respetar `Retry-After` en 429.

## 2. Fase 0: preparación

1. Commitear las migraciones pendientes antes de añadir una nueva, para no perder el orden:
   - `SQL/migrations/2026-09-16_steam_games_lowest_price.sql`
   - `SQL/migrations/2026-09-16_steam_games_image_url.sql`
   - `Deals.Web/app/api/bff/steam/suggestions/`
2. Confirmar con la key real si `prices/v3?country=MX` devuelve `USD` o `MXN`. Cambia solo el valor por defecto de `Itad:Country`, no el diseño.

## 3. Fase 1: cliente ITAD

Archivos nuevos:

- `Deals.API/ItadOptions.cs` — `ApiKey`, `BaseUrl` (default `https://api.isthereanydeal.com/`, HTTPS obligatorio), `Country` (default `MX`), `RefreshAfterDays` (default `7`), `TimeoutSeconds` (default `10`), `OfficialShopIds`.
- `Deals.BusinessLogic/Interfaces/IItadClient.cs`
  - `Task<string?> LookupSteamAppIdAsync(int appId, CancellationToken ct)`
  - `Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(IReadOnlyCollection<string> itadIds, CancellationToken ct)`
- `Deals.BusinessLogic/Models/Itad/ItadGamePrices.cs` — `ItadId`, `HistoryLowes`, `Deals`.
- `Deals.BusinessLogic/Models/Itad/ItadDeal.cs` — `ShopId`, `ShopName`, `IsOfficial`, `Currency`, `RegularPriceMinor`, `CurrentPriceMinor`, `DiscountPercent`, `DealUrl`, `ObservedAt`.
- `Deals.BusinessLogic/Services/ItadClient.cs` — sigue la forma de `SteamStoreClient` (HttpClient tipado, helpers de parseo defensivo con `JsonDocument`).

Modificados:

- `Deals.API/Extensions/ServiceCollectionExtensions.cs` — `AddHttpClient<IItadClient, ItadClient>` con validación HTTPS, clamp de timeout y fail-fast si `ApiKey` es placeholder.
- `Deals.API/Program.cs` — `Configure<ItadOptions>`.
- `Deals.API/appsettings.json` — placeholders `SET_ITAD_API_KEY` y `"System.Net.Http.HttpClient": "Warning"` para que la key no acabe en logs.

Reglas:

- Key por header `ITAD-API-Key`, nunca en query string.
- `HttpRequestException` se propaga para que el controlador devuelva 503 con mensaje genérico (patrón ya usado por Steam).

Criterio de aceptación: `lookup` y `prices` devuelven datos correctos para el AppID `3375780` contra la API real.

## 4. Fase 2: persistencia e integración al detalle

Migración `SQL/migrations/2026-09-17_itad_offers.sql` (idempotente) y bloque correspondiente en `SQL/schema.sql`:

```sql
ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS itad_game_id VARCHAR(36),
    ADD COLUMN IF NOT EXISTS offers_refreshed_at TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS public.game_offers (
    game_offer_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES public.steam_games(steam_game_id) ON DELETE CASCADE,
    source VARCHAR(16) NOT NULL DEFAULT 'itad',
    shop_id VARCHAR(32) NOT NULL,
    shop_name VARCHAR(128) NOT NULL,
    is_official BOOLEAN NOT NULL DEFAULT FALSE,
    currency VARCHAR(3) NOT NULL,
    regular_price_minor INT,
    current_price_minor INT,
    discount_percent INT,
    deal_url VARCHAR(1024),
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_game_offers UNIQUE (steam_game_id, source, shop_id, currency)
);

CREATE INDEX IF NOT EXISTS idx_game_offers_game ON public.game_offers(steam_game_id);
ANALYZE public.game_offers;
```

Entidad y mapeo:

- `Deals.Models/Entities/GameOffer.cs` — `[Table("game_offers")]`, hereda `BaseModel`, todas las columnas mapeadas.
- `Deals.Models/Entities/SteamGame.cs` — añadir `ItadGameId`, `OffersRefreshedAt`, nav `ICollection<GameOffer> Offers`.
- `Deals.BusinessLogic/Context/AppDbContext.cs` — `DbSet<GameOffer>`, relación 1-N con cascade, índice por `SteamGameId`.

Servicio y endpoint:

- `Deals.BusinessLogic/Services/SteamGameService.cs` — `GetByAppIdAsync(int appId, bool forceRefresh, CancellationToken ct)`. Antes de llamar a ITAD:
  ```
  needsOffers = forceRefresh
             || game.ItadGameId is null
             || ofertas.Count == 0
             || game.OffersRefreshedAt < UtcNow.AddDays(-officialRefreshDays)
  ```
  - Steam se sigue consultando siempre, como hoy.
  - ITAD envuelto en `try/catch`: un fallo devuelve Steam + ofertas persistidas + `offersStale = true`. Nunca oculta el precio de Steam.
  - Si `lookup` devuelve `null` o el tipo no es `game`, no se guardan ofertas y se marca el estado explícitamente.
  - `game_offers` es **snapshot por tienda** (upsert por la unique key). El historial append-only sigue siendo `steam_price_observations`.
- `Deals.API/Controllers/SteamController.cs` — `GET api/steam/games/{appId:int}?refresh=true`, misma política `UserWithId`.
- `Deals.API/Models/Steam/SteamGameResponse.cs` — añadir `Offers`, `OffersRefreshedAt`, `OffersStale`.

Orden de despliegue obligatorio: DDL primero, aplicación después. La versión nueva falla con `42703 column does not exist` si se despliega antes.

Criterio de aceptación: abrir `/games/3375780` la primera vez consulta ITAD; las siguientes dentro de 7 días no llaman a ITAD; `?refresh=true` fuerza la llamada; con ITAD caído el detalle sigue mostrando Steam.

## 5. Fase 3: conversión USD→MXN

Migración `SQL/migrations/2026-09-18_fx_rates.sql` y `SQL/schema.sql`:

```sql
CREATE TABLE IF NOT EXISTS public.fx_rates (
    base VARCHAR(3) NOT NULL,
    quote VARCHAR(3) NOT NULL,
    rate NUMERIC(18,8) NOT NULL,
    rate_date DATE NOT NULL,
    source VARCHAR(32) NOT NULL,
    fetched_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (base, quote, rate_date)
);
```

Proveedores:

| Rol | Fuente | Endpoint | Auth |
|---|---|---|---|
| Primario | Banxico SIE, serie `SF43718` (FIX oficial) | `GET {BaseUrl}/SieAPIRest/service/v1/series/SF43718/datos/oportuno` | header `Bmx-Token` |
| Fallback | Frankfurter v2, provider Banxico | `GET https://api.frankfurter.dev/v2/providers/banxico/rate/usd/mxn` | ninguna |

Notas de implementación:

- `dato` de Banxico es **string** y la fecha viene `dd/MM/yyyy`. Normalizar a `decimal` y a fecha ISO.
- FIX se publica ~12:00 CDMX en días hábiles. Frankfurter lagea ~1 día: solo fallback.
- Límites Banxico: 200 req/5 min, 10,000/día. Al excederse bloquea el token.

Archivos:

- `Deals.Models/Entities/FxRate.cs`
- `Deals.BusinessLogic/Interfaces/IFxRateService.cs` — `Task<FxRate?> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken ct)`
- `Deals.BusinessLogic/Interfaces/IFxRateProvider.cs` + `Deals.BusinessLogic/Services/Fx/BanxicoFxRateProvider.cs` y `FrankfurterFxRateProvider.cs`
- `Deals.BusinessLogic/Services/FxRateService.cs` — lee de `fx_rates`, y si no hay fila del día delega al proveedor primario con fallback.
- `Deals.API/FxOptions.cs` — `BanxicoToken`, `BaseUrl`, `BaseCurrency` (`USD`), `QuoteCurrency` (`MXN`).
- `Deals.API/HostedServices/FxRateRefreshJob.cs` — job diario. El fetch nunca ocurre por request de usuario.
- `AppDbContext` + DI + `Program.cs`.

Regla de presentación: el precio original (`currency` + `price_minor`) **nunca** se sobrescribe. El convertido se expone como valor derivado junto con la tasa y la fecha usadas.

Criterio de aceptación: `fx_rates` tiene la fila del día; con Banxico caído se usa Frankfurter sin romper el detalle.

## 6. Fase 4: BFF y UI

BFF:

- `Deals.Web/app/api/bff/steam/games/[appId]/route.ts` — leer `refresh` de `new URL(request.url).searchParams` y propagarlo. GET + query param evita tocar middleware y CSRF.

Contratos:

- `Deals.Web/lib/contracts/steam.ts` — añadir `SteamGameOffer`, `offers`, `offersRefreshedAt`, `offersStale` y el bloque de conversión. Reutilizar los normalizadores estrictos existentes.

UI — `Deals.Web/app/games/[steamAppId]/game-client.tsx`:

- Mantener la sección Steam actual sin cambios.
- Añadir sección de ofertas agrupada por `isOfficial`, con encabezados **"Tiendas oficiales / autorizadas"** y **"Otras tiendas autorizadas"**. No usar la palabra "keys": ITAD no cubre grey market.
- Columnas: `Tienda` (link a `dealUrl`) | `Precio base` | `Descuento` | `Precio` | `Moneda` | `Aprox. MXN` | `Observado`.
- `dealUrl` se usa tal cual, con `target="_blank"` y `rel="noopener noreferrer"`. Nunca quitar el tag de afiliado.
- Botón **"Actualizar ofertas"** que llama con `refresh: true`, con estado de carga.
- Badge `Datos posiblemente desactualizados` cuando `offersStale`.
- Atribución visible: "Datos de precios: IsThereAnyDeal".
- Actualizar `Deals.Web/DESIGN.md` como parte del cambio.

Criterio de aceptación: `pnpm build` pasa; la tabla muestra agrupación, moneda, conversión y atribución; el botón de refresh fuerza llamada real.

## 7. Fase 5: notificaciones por Telegram

Se copia y adapta desde `/home/jams45072/Proyectos/Gastos`:

| Origen en Gastos | Uso en DealExt |
|---|---|
| `GastosApp.API/Configuration/TelegramOptions.cs` | Base para `Deals.API/TelegramOptions.cs`. **Simplificar**: DealExt solo envía, no recibe comandos, así que solo necesita `Enabled`, `BotToken`, `ChatId`. No hacen falta `AllowedUserId` ni `AppUserId`. |
| `GastosApp.API/Extensions/TelegramConfigurationExtensions.cs` | Bind + `ValidateOnStart` condicionado a `Enabled` + guard de placeholders `SET_`. |
| `GastosApp.API/Services/Telegram/TelegramBotClientProvider.cs` | Copiar tal cual: cliente lazy, truncado a 4000 caracteres, nunca loguea el token (solo `exception.GetType().Name`). |
| `GastosApp.API/Controllers/TelegramWebhookController.cs` | **No copiar.** No se exponen webhooks en este alcance. |

Datos:

- Migración `SQL/migrations/2026-09-19_price_alerts.sql`:

```sql
CREATE TABLE IF NOT EXISTS public.price_alerts (
    price_alert_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES public.steam_games(steam_game_id) ON DELETE CASCADE,
    target_price_minor INT NOT NULL,
    currency VARCHAR(3) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    last_notified_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_price_alerts UNIQUE (steam_game_id, currency)
);
```

Scheduler:

- `Deals.API/HostedServices/PriceAlertScheduler.cs` — `BackgroundService`.
- Cada corrida: tomar alertas activas, agrupar por moneda y consultar `prices/v3` en lotes de hasta 200 UUIDs (1 llamada por lote).
- Comparar contra `target_price_minor`; respetar `last_notified_at` para no repetir.
- Notificar con el mismo formato de mensaje que usa `Gastos` (`TelegramBotClientProvider.TrySendAsync`).
- Registrar como hosted service en `Program.cs`.

Criterio de aceptación: una alerta con umbral alcanzable envía un mensaje real y no se repite en la siguiente corrida.

## 8. Configuración

`.env.example` raíz (nunca en `Deals.Web/.env.example`):

```
ITAD__ApiKey=SET_ITAD_API_KEY
ITAD__Country=MX
ITAD__RefreshAfterDays=7
ITAD__OfficialShopIds=61,35,16,62,48,6,36,20,50,64
FX__BanxicoToken=SET_BANXICO_TOKEN
FX__BaseCurrency=USD
FX__QuoteCurrency=MXN
Telegram__Enabled=false
Telegram__BotToken=SET_TELEGRAM_BOT_TOKEN
Telegram__ChatId=0
```

- `docker-compose.yml` — estas claves solo en `api.environment`.
- `Deals.API/appsettings.json` — solo placeholders `SET_*`.
- La key de ITAD y el token de Banxico son exclusivamente server-side. Nunca en el BFF ni en el bundle del navegador.

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un appid mapea a un pack o familia distinta | Persistir `itad_game_id`, comparar título y marcar como no comparable en vez de unir en silencio |
| `lookup` devuelve `null` (delisted, DLC, demo) | Limitar a `type == "game"` y mostrar estado explícito |
| Juego `is_free` con ofertas de pago al lado | Decidir explícitamente no mostrar ofertas de tiendas de pago en ese caso |
| Tienda oficial fuera del allowlist se etiqueta mal | Mostrar siempre el nombre de la tienda y permitir ajustar `OfficialShopIds` |
| Un bug dispara N llamadas por render | Gate de 7 días + refresh manual + bucket global in-process (1 req/s, burst 10) + respetar `Retry-After` |
| Moneda distinta a MXN presentada como regional | Mostrar la columna de moneda siempre y etiquetar el convertido como aproximado, con tasa y fecha |
| Divergencia de índices entre `AppDbContext` y `schema.sql` | Aprovechar la migración de ITAD para alinearlos |
| ITAD cambia o revoca acceso | Fallback a Steam y ofertas persistidas; el detalle nunca queda vacío |

## 10. Fuera de alcance

- Keyshops y grey market (ITAD no los cubre; requiere gg.deals u otro proveedor).
- OAuth2 con ITAD y webhooks `notification-waitlist`.
- Bundles externos (`prices/v3` no los expone).
- Conversión FX histórica (solo la tasa del día).
- Múltiples divisas además de USD→MXN.

## 11. Verificación

| Fase | Evidencia |
|---|---|
| 0 | Migraciones pendientes commiteadas; moneda real de `country=MX` confirmada |
| 1 | Llamada real a `lookup` y `prices` para el AppID `3375780` |
| 2 | `dotnet build Deals.sln`; detalle con y sin ITAD disponible; gate de 7 días; `?refresh=true`; DDL aplicado antes del deploy |
| 3 | Fila del día en `fx_rates`; fallback a Frankfurter con Banxico no disponible |
| 4 | `pnpm build` en `Deals.Web`; tabla con agrupación, moneda, conversión, refresh y atribución |
| 5 | Mensaje real de Telegram enviado una sola vez por alerta |

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```
