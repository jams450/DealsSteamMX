# DealExt: plan de wishlist, biblioteca poseída y alertas

Estado: propuesto, pendiente de aprobación. Solo análisis y plan.

Alcance: importar la wishlist de Steam, saber si un juego ya está poseído en **cualquier** tienda, y notificar ofertas por Telegram. El export de la biblioteca se produce con un script propio de Playnite y se carga a mano cada semana.

Continúa a `PLAN_ITAD.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Decisiones tomadas

| Decisión | Razón |
|---|---|
| **Wishlist = Steam**, no ITAD | `IWishlistService/GetWishlist/v1` no requiere key ni OAuth ni cifrado de tokens. Elimina el flujo OAuth completo y la tabla de cuentas externas. |
| **Se descarta la waitlist de ITAD** (OAuth `wait_read`/`wait_write`) | Aporta lo mismo que la wishlist de Steam a cambio de PKCE, app registrada, redirect URI y cifrado de refresh tokens. |
| **ITAD se queda como resolvedor de identidad**, no como fuente de wishlist | Es lo único que permite saber si un juego ya está en otra tienda. Ver §3. |
| Export de Playnite con **script propio** | El export nativo de Playnite no es JSON y no incluye IDs de tienda. Ver §5. |
| Carga manual semanal | Decisión explícita: sin watcher, sin cron, sin sincronización automática. |
| Todo queda anclado a **una sola cuenta** (el admin) | DealExt no tiene registro de usuarios finales. No se abre registro para esto. |

## 2. Lo que ya existe y se reutiliza

Las fases 1–4 de `PLAN_ITAD.md` están **implementadas** (staged, sin commitear). Lo que este plan aprovecha:

| Ya implementado | Cómo se usa aquí |
|---|---|
| `steam_games.itad_game_id` (`VARCHAR(36)`) | Es la identidad canónica cross-store. Se persiste al abrir el detalle, así que la consulta "¿ya lo tengo?" es un `SELECT` local sin llamadas a la API. |
| `IItadClient.LookupSteamAppIdAsync(int appId, CancellationToken)` | Resuelve el UUID de un appid de Steam. **El shop id 61 está hardcodeado** en el path `lookup/id/shop/61/v1`. |
| `ProviderRequestGovernor` — token bucket 1 req/s, burst 10 | Reutilizable tal cual para el import. Ya hace retry en 429 con `Retry-After`. |
| `ItadOptions.OfficialShopIds` | Sirve de base para la tabla de `store` → shopId del import. |
| `game_offers` — snapshot por `(steam_game_id, source, offer_key)` con precios originales y derivados en MXN | Fuente de la comparación de precios de las alertas. |
| `IFxRateService.GetLatestRateAsync(...)` — contrato de solo lectura, **sin fetch**, para requests de usuario | Usar solo esa variante desde el scheduler y desde la UI. `GetRateAsync` es el que hace fetch. |
| `Deals.API/HostedServices/FxRateRefreshJob.cs` | Plantilla del `PriceAlertScheduler`: mismo patrón de `BackgroundService`, delay de arranque y scope por ciclo. |
| `POST /api/steam/games/{appId}/refresh` + BFF `POST` con `csrfFetch` | Precendente real para endpoints que mutan estado desde la UI. |
| `Deals.Web/lib/contracts/steam.ts` — normalizadores estrictos (`toIsoDate`, `toIsoDateTime`, `toHttpsUrl`) | Extender con los tipos de biblioteca; no relajar la validación. |

**No existe nada de** Telegram, `price_alerts`, `user_library`, `user_external_accounts`, wishlist ni biblioteca. Verificado por grep y contra `SQL/schema.sql`.

### Dependencia que hay que resolver antes del import

`IItadClient` **no puede resolver IDs de tiendas que no sean Steam**: el shop id 61 está fijo en el path. El import de Playnite necesita un método nuevo, del estilo:

```csharp
Task<IReadOnlyDictionary<string, string?>> LookupByShopAsync(
    int shopId, IReadOnlyCollection<string> shopGameIds, CancellationToken ct);
```

Es una extensión del cliente existente, no un cliente nuevo. La clasificación por allowlist y el governor se reaprovechan sin cambios.

## 3. La pregunta central: ¿se puede saber si ya lo tengo en otra tienda?

**Sí.** Verificado en vivo contra la API real:

```
POST /lookup/id/shop/61/v1   ["app/1091500"]      → {"app/1091500":   "018d937f-2997-7131-b8b9-7c8af4825fa8"}
POST /lookup/id/shop/35/v1   ["1423049311"]       → {"1423049311":    "018d937f-2997-7131-b8b9-7c8af4825fa8"}
```

Cyberpunk 2077 en Steam y en GOG devuelven **el mismo UUID**. Ese UUID es la identidad canónica cross-store.

Propiedades confirmadas del endpoint:

| Propiedad | Evidencia |
|---|---|
| Sin API key | Funciona sin credenciales |
| Acepta lotes | `["app/1091500","app/3375780","app/999999999"]` → 3 resultados en **1 llamada** |
| Devuelve `null` para lo desconocido | `app/999999999` → `null` |
| Formato de id por tienda | Steam usa `app/<appid>`; GOG usa el product id numérico a secas |

Shop IDs confirmados en `/service/shops/v1?country=MX`:

| Tienda | shopId |
|---|---|
| Steam | 61 |
| GOG | 35 |
| Epic Game Store | 16 |
| Ubisoft Store | 62 |
| Microsoft Store | 48 |
| EA Store | 52 |
| Blizzard | 4 |
| Humble Store | 37 |

### Flujo de la consulta "¿ya lo tengo?"

```
Usuario abre /games/<steamAppId>
  └ steam_games.itad_game_id  (ya persistido por la integración ITAD)
       └ SELECT store, is_installed FROM user_library WHERE itad_game_id = <uuid>
            └ "Ya lo tienes en GOG (no instalado)"
```

Cero llamadas a la API: el UUID sale de la fila que ya existe, y la biblioteca es local.

### Limitación honesta

El cruce depende de que ITAD conozca el juego. Devuelve `null` para títulos que no están en su catálogo, y ahí la respuesta es "no se sabe", no "no lo tienes". Plan de contingencia, en orden:

1. Coincidencia por UUID de ITAD (fiable).
2. Si `null`: coincidencia por título normalizado (minúsculas, sin sufijos de edición, sin puntuación). Se muestra como **"posible coincidencia"**, nunca como confirmada.
3. Si tampoco: no se muestra nada. No afirmar que no lo tienes.

## 4. Fase 1: identidad cross-store

Una sola tabla nueva, sin refactor del esquema actual.

```sql
-- SQL/migrations/2026-09-XX_user_library.sql  (+ bloque en SQL/schema.sql)
CREATE TABLE user_library (
    user_library_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    itad_game_id VARCHAR(36),                 -- identidad canónica; NULL si ITAD no lo conoce
    store VARCHAR(32) NOT NULL,               -- steam | gog | epic | ubisoft | microsoft | ea | blizzard
    store_game_id VARCHAR(64) NOT NULL,       -- appid, product id, catalog id...
    title VARCHAR(256) NOT NULL,
    state VARCHAR(16) NOT NULL,               -- wished | owned
    is_installed BOOLEAN,
    priority INT,                             -- prioridad de wishlist de Steam
    added_at TIMESTAMPTZ,
    imported_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_user_library UNIQUE (user_id, store, store_game_id, state)
);

CREATE INDEX idx_user_library_itad ON user_library(user_id, itad_game_id);

-- Coincidencia por título cuando ITAD no conoce el juego
CREATE INDEX idx_user_library_title ON user_library(user_id, lower(title));
```

- Entidad `Deals.Models/Entities/UserLibrary.cs` (`BaseModel`, tipo `"user_library"`).
- `store` es texto libre validado en el servicio, **no** un enum de base de datos: añadir una tienda no debe requerir migración.
- La resolución a UUID se hace **en el import**, no en la consulta. Una vez resuelto, la consulta es un `SELECT` local.

## 5. Fase 2: importar la biblioteca de Playnite

### Por qué hace falta un script propio

El *Library Exporter* oficial de Playnite (`LibraryExporterPS_Builtin`, de JosefNemec) es una script extension que emite **CSV o TXT** con solo `Name, Source, ReleaseDate, Playtime, IsInstalled`. **No incluye ID de tienda**, así que tal cual no sirve para cruzar nada. Tampoco existe exportación JSON nativa.

Solución: script extension propio de ~10 líneas en PowerShell sobre `$PlayniteApi.Database.Games` con `ConvertTo-Json`, exportando:

| Campo | Por qué |
|---|---|
| `GameId` | **Es el ID de tienda.** Para juegos de Steam, el appid: el plugin oficial de Steam hace `new GameID(ulong.Parse(game.GameId))` |
| `PluginId` / `Source` | Distingue la tienda de origen |
| `Name` | Fallback de coincidencia por título |
| `IsInstalled` | Mostrar "instalado" vs "en biblioteca" |
| `Added` | Fecha de alta |

Ese script se guarda en el repo como documentación operativa, no se ejecuta desde DealExt.

### Import

- `POST /api/library/import` — **`AdminWithId`**. Recibe el JSON, hace upsert y descarta el archivo. No se persiste el JSON crudo.
- Validación: tamaño máximo, JSON válido, y descarte silencioso de entradas sin `GameId` o sin `PluginId`.
- Resolución de UUID por tienda, agrupando por `store` y llamando `lookup/id/shop/<id>/v1` en lotes. Una llamada por tienda por cada ~200 entradas.
- Idempotente: reimportar el mismo JSON no duplica (`UNIQUE (user_id, store, store_game_id, state)`).
- Reporte de resultado: cuántos importados, cuántos resueltos a UUID, cuántos sin resolver. Sin eso, un import roto pasa desapercibido.

### Pendiente de verificar antes de fijar el contrato

1. **Formato del id que ITAD espera por tienda.** Steam es `app/<appid>` y GOG es el product id numérico: confirmado. Para Epic, Ubisoft, Microsoft y EA **no está verificado**.
2. **Qué emite Playnite en `GameId` para Epic y Ubisoft.** El plugin de Epic usa un identificador propio que puede no ser el catalog id que ITAD espera. Hay que contrastar con datos reales antes de escribir el import.

Hasta verificar eso, el import debe guardar la fila igual y dejar `itad_game_id` en `NULL`, con el reporte marcándolo como no resuelto.

## 6. Fase 3: wishlist de Steam

| Aspecto | Detalle |
|---|---|
| Endpoint | `GET https://api.steampowered.com/IWishlistService/GetWishlist/v1?steamid=<steamid64>` |
| Key | **No requiere** |
| Requisitos | `steamid64` (no vale la URL vanity); perfil **y wishlist públicos** |
| Devuelve | appid, prioridad, fecha de alta. **No da precios.** |
| Confiabilidad | Registrado en la API de Valve (`GetSupportedAPIList`) pero **sin documentación pública**: `partner.steamgames.com/doc/webapi/IWishlistService` redirige a `/doc` |
| Trampa | Wishlist privada o vacía → `{"response":{}}`. **Indistinguible de "sin juegos".** Hay que mostrar el estado explícito, no "0 juegos". |
| Descartado | `store.steampowered.com/wishlist/profiles/<id>/wishlistdata` redirige roto sin cookies de sesión |

**Pendiente de verificar**: la forma exacta de `items[]` con un perfil público real, antes de fijar el contrato.

Diseño:

- `steamid64` como valor de configuración (`Steam__WishlistSteamId`), no una tabla: es un dato único del admin.
- Se guarda como `store_game_id` con `store = 'steam'` y `state = 'wished'`, con la prioridad de Steam.
- Los appids nuevos se insertan en `steam_games` si no existen, para que el detalle y las alertas funcionen igual.
- `steam_games.itad_game_id` se resuelve con el mismo `lookup` keyless, en lote.

## 7. Fase 4: alertas y Telegram

- Tabla `price_alerts`: `user_id`, `steam_game_id`, `target_price_minor`, `currency`, `max_price_type` (`current` | `history_low`), `is_active`, `last_notified_at`.
- `Deals.API/HostedServices/PriceAlertScheduler.cs` (`BackgroundService`), junto a `FxRateRefreshJob` y siguiendo su misma estructura (delay de arranque, `CreateScope` por ciclo, catch que no tumba el proceso).
- Corrida: juegos de `user_library` con `state = 'wished'` + `price_alerts` activas → agrupar por moneda → `prices/v3` en lotes de hasta **200 UUIDs por llamada**, pasando por `ProviderRequestGovernor`.
- Consultar el precio con `IFxRateService.GetLatestRateAsync`, **nunca** `GetRateAsync`: el contrato es que los requests de usuario y los jobs periódicos no disparan fetch de FX.
- **Excluir los juegos poseídos.** `user_library` con `state = 'owned'` filtra antes de consultar: no se alerta de lo que ya se tiene.

### Dependencia para alertar por mínimo histórico

`max_price_type = 'history_low'` necesita que el `historyLow` de ITAD **se persista**. Hoy `ItadGamePrices.HistoryLowes` se parsea en cada llamada a `prices/v3` y se descarta: no hay columnas en `game_offers` ni lo consume nadie.

Dos caminos válidos:

1. Persistir `history_low_all_minor` y `history_low_currency` en `game_offers` (migración fechada). Viene gratis en cada respuesta, así que no añade llamadas.
2. Limitar las alertas a precio objetivo (`current`) y eliminar el parseo de `historyLow` como código muerto.

Decidir antes de construir el scheduler, porque condiciona el esquema de `price_alerts`.

- Telegram: copiar de `/home/jams45072/Proyectos/Gastos`:
  - `GastosApp.API/Configuration/TelegramOptions.cs` → `Deals.API/TelegramOptions.cs`. **Simplificado**: DealExt solo envía, así que basta `Enabled`, `BotToken`, `ChatId`. No hacen falta `AllowedUserId` ni `AppUserId`.
  - `GastosApp.API/Extensions/TelegramConfigurationExtensions.cs` → bind + `ValidateOnStart` condicionado a `Enabled` + guard de placeholders `SET_`.
  - `GastosApp.API/Services/Telegram/TelegramBotClientProvider.cs` → tal cual: cliente lazy, truncado a 4000 caracteres, nunca loguea el token.
  - `TelegramWebhookController.cs` → **no se copia**. No se exponen webhooks.
- Nunca notificar dos veces la misma oferta: comparar contra `game_offers` previo y `last_notified_at`.

## 8. Fase 5: UI

- `Deals.Web/app/games/[steamAppId]/game-client.tsx`: badge **"Ya lo tienes en GOG"** cuando `user_library` lo confirme; **"Posible coincidencia"** cuando sea por título.
- Página de biblioteca: dos listas, deseados y poseídos, con filtro por tienda.
- Página de import: subir el JSON, mostrar el reporte (importados / resueltos / sin resolver).
- Nunca mostrar el JSON crudo ni rutas del sistema de archivos del usuario.
- Actualizar `Deals.Web/DESIGN.md` como parte del cambio.

## 9. Orden de fases

```
Fase 0   commitear ITAD + FX y aplicar sus migraciones
         decidir: clasificación "authorized" muerta y persistir o eliminar historyLow
   │
   ├─ Fase 3  wishlist de Steam        ← sin dependencias, valor inmediato
   │     └─ Fase 4  alertas + Telegram ← requiere Fase 3
   │
   └─ Fase 1  user_library + identidad ← esquema
         └─ Fase 2  import de Playnite    ← requiere Fase 1 + lookup por tienda
               └─ Fase 5  UI de biblioteca y badges  ← requiere 1, 2 y 3
```

**Fase 0 no es opcional.** Todo el trabajo de ITAD y FX está staged sin commitear, y este plan añade migraciones con fecha. Si se commitea después, se pierde el orden de las fechas en `SQL/migrations/`.

Las dos decisiones de Fase 0 condicionan el resto:

- La rama `classification = "authorized"` es inalcanzable (las ofertas se piden filtrando por el allowlist oficial). Eliminarla simplifica, pero si se elige abrir a tiendas autorizadas después, hay que quitar el parámetro `shops=` de la consulta a `prices/v3` y reactivar la banda en la UI.
- `historyLow` ya se descarga y se descarta. Persistirlo habilita alertas por mínimo histórico casi sin coste; si no, se elimina el parseo y `price_alerts.max_price_type` se queda solo con `current`.

Empezar por la wishlist de Steam: no necesita OAuth, ni cifrado de tokens, ni el import, y ya produce alertas útiles. La identidad cross-store y el import llegan después.

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| ITAD no conoce el juego → `null` | Fallback por título normalizado, marcado como "posible coincidencia". Nunca afirmar "no lo tienes". |
| Formato de id por tienda no verificado (Epic, Ubisoft, Microsoft, EA) | Verificar con datos reales de Playnite antes de fijar el contrato; guardar la fila igual con `itad_game_id` en `NULL` |
| `GameId` de Playnite para Epic no es el catalog id que ITAD espera | Contraste real obligatorio; el reporte de import expone cuántos quedaron sin resolver |
| Wishlist de Steam privada devuelve `{}` | Distinguir explícitamente "privada o vacía" en la UI |
| API de wishlist sin documentación pública ni límites publicados | Aislar tras un cliente propio; ante 429 o cambio de forma, degradar sin romper el resto |
| El export de Playnite cambia de forma entre versiones | El import valida y reporta; no falla en silencio |
| Cuota ITAD consumida por polling | Lotes, intervalo en horas, TTL, respetar `Retry-After` |
| Cruce por título da falsos positivos (ediciones, remasters) | Nunca presentarlo como confirmado |

## 11. Fuera de alcance

- OAuth con ITAD y su waitlist.
- Registro público de usuarios o multiusuario.
- Refactor a una entidad `games` neutra: el anclaje actual a `steam_games` se mantiene.
- Cálculo de bundles según juegos poseídos.
- Sincronización automática del export de Playnite (manual y semanal por decisión).
- Keyshops y grey market.

## 12. Verificación

| Fase | Evidencia |
|---|---|
| 1 | Migración aplicada; `user_library` con `itad_game_id` resuelto para un juego real en 2 tiendas |
| 2 | Importar el JSON del script; el conteo cuadra con Playnite; el reporte indica resueltos y no resueltos; reimportar no duplica |
| 3 | Importar una wishlist pública real; perfil privado muestra el estado explícito |
| 4 | Una alerta alcanzable produce un único mensaje de Telegram; un juego poseído no genera alerta |
| 5 | `pnpm build`; badge de "ya lo tienes" con datos reales de GOG |

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```

## Anexo: script de export para Playnite

Referencia funcional, no probada en runtime. Se guarda como script extension en Playnite.

```powershell
function ExportLibrary {
    param($PlayniteApi)
    $games = $PlayniteApi.Database.Games | ForEach-Object {
        [pscustomobject]@{
            GameId      = $_.GameId
            PluginId    = $_.PluginId
            Source      = $_.Source
            Name        = $_.Name
            IsInstalled = $_.IsInstalled
            Added       = $_.Added
        }
    }
    $path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'dealext-library.json'
    $games | ConvertTo-Json -Depth 4 | Set-Content -Path $path -Encoding UTF8
}
```

`GameId` es el ID de tienda y `PluginId` la tienda de origen: esos dos campos son el motivo de no usar el export oficial.
