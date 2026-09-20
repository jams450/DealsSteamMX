# DealExt: plan de wishlist de Steam y alertas

Estado: **implementado** (tabla `user_library`, `users.steam_id64`, sync de la wishlist, controller, `/wishlist` + BFF + contratos, `history_low_all_minor`/`history_low_currency`) y commiteado. Lo que **no** está implementado, verificado contra `SQL/schema.sql` y el código: `price_alerts` (la tabla no existe), Telegram, `user_external_accounts` y la UI de alertas. La **biblioteca de juegos comprados** sí lo está → `PLAN_LIBRARY.md`.
ya no vive aquí: se movió a `PLAN_LIBRARY.md`.

Alcance: importar la wishlist de Steam y notificar ofertas por Telegram. La lista de juegos comprados, su
import y el binding con precios son alcance de `PLAN_LIBRARY.md`; este plan solo los **consume** (excluir lo
poseído de las alertas).

El producto tiene dos partes: **wishlist y precios** (este plan) y **biblioteca con reseñas**
(`PLAN_LIBRARY.md`).

Continúa a `PLAN_ITAD.md` y `PLAN_LIBRARY.md`. La identidad de juego compartida vive en
`PLAN_CATALOG.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Decisiones tomadas

| Decisión | Razón |
|---|---|
| **Wishlist = Steam**, no ITAD | `IWishlistService/GetWishlist/v1` no requiere key ni OAuth ni cifrado de tokens. Elimina el flujo OAuth completo y la tabla de cuentas externas. |
| **Se descarta la waitlist de ITAD** (OAuth `wait_read`/`wait_write`) | Aporta lo mismo que la wishlist de Steam a cambio de PKCE, app registrada, redirect URI y cifrado de refresh tokens. |
| **ITAD se queda como resolvedor de identidad**, no como fuente de wishlist | Es lo único que permite saber si un juego ya está en otra tienda. El diseño completo está en `PLAN_LIBRARY.md` §10, diferido. |
| **La biblioteca poseída sale de este plan** | Lista de juegos comprados, import de Playnite y binding se documentan en `PLAN_LIBRARY.md`. Aquí solo se consumen. |
| **Game Pass no es posesión** | Se guarda como `state='subscription'` con `store='xbox'`: entra en la biblioteca con su tag propio y queda fuera de precios y de alertas. Detalle en `PLAN_LIBRARY.md` §5. |
| **Identidad de juego compartida** | `PLAN_CATALOG.md` crea `games` + `game_external_ids` para que wishlist y biblioteca lean la **misma ficha de juego**, y para que HowLongToBeat o IGDB encajen después sin refactor. |
| Carga manual semanal del export de Playnite | Decisión explícita: sin watcher, sin cron, sin sincronización automática. Detalle en `PLAN_LIBRARY.md`. |
| Todo queda anclado a **una sola cuenta** (el admin) | DealExt no tiene registro de usuarios finales. No se abre registro para esto. |

## 2. Lo que ya existe y se reutiliza

Las fases 1–4 de `PLAN_ITAD.md` están **implementadas, commiteadas y en producción**. Lo que este plan aprovecha:

| Ya implementado | Cómo se usa aquí |
|---|---|
| `steam_games.itad_game_id` (`VARCHAR(36)`) | Es la identidad canónica cross-store. Se persiste al abrir el detalle, así que la consulta "¿ya lo tengo?" es un `SELECT` local sin llamadas a la API. |
| `IItadClient.LookupSteamAppIdAsync(int appId, CancellationToken)` | Resuelve el UUID de un appid de Steam. **El shop id 61 está hardcodeado** en el path `lookup/id/shop/61/v1`. La variante multi-tienda es de `PLAN_LIBRARY.md` §10. |
| `ProviderRequestGovernor` — token bucket 1 req/s, burst 10 | Reutilizable tal cual. Ya hace retry en 429 con `Retry-After`. |
| `ItadOptions.OfficialShopIds` | Filtra **ofertas** de `prices/v3`. No confundirlo con el lookup de identidad, que acepta más tiendas. |
| `game_offers` — snapshot por `(steam_game_id, source, offer_key)` con precios originales y derivados en MXN | Fuente de la comparación de precios de las alertas. |
| `IFxRateService.GetLatestRateAsync(...)` — contrato de solo lectura, **sin fetch**, para requests de usuario | Usar solo esa variante desde el scheduler y desde la UI. `GetRateAsync` es el que hace fetch. |
| `Deals.API/HostedServices/FxRateRefreshJob.cs` | Plantilla del `PriceAlertScheduler`: mismo patrón de `BackgroundService`, delay de arranque y scope por ciclo. |
| `POST /api/steam/games/{appId}/refresh` + BFF `POST` con `csrfFetch` | Precendente real para endpoints que mutan estado desde la UI. |
| `Deals.Web/lib/contracts/steam.ts` — normalizadores estrictos (`toIsoDate`, `toIsoDateTime`, `toHttpsUrl`) | Extender con los tipos nuevos; no relajar la validación. |
| `WishlistSyncService` + `IWishlistSyncService` + `WishlistSyncJob` | Escribe `state='wished'` y **nunca toca** filas `owned`. Es la base de este plan. |
| `users.steam_id64` + `wishlist_synced_at` + `wishlist_state` (`WishlistStates.Compose`) | Identidad Steam del admin y estado visible de la sincronización. |

### Estado real del checkout

Corrige lo que este plan afirmaba antes de la auditoría:

| Pieza | Estado |
|---|---|
| `user_library` (tabla + entidad + `AppDbContext` + `uq_user_library` + índices) | **Implementado** — `SQL/migrations/2026-09-24_wishlist_and_steam_id.sql` |
| `users.steam_id64`, `wishlist_synced_at`, `wishlist_state`, `min_viable_discount_percent` | **Implementado** |
| Wishlist de Steam: sync, controller, `/wishlist` + BFF + contratos | **Implementado y commiteado** |
| Ruta `state='wished'` de `user_library` | **Implementado**; el prune solo borra filas `wished` |
| `game_offers.history_low_all_minor` / `history_low_currency` | **Implementado** — cierra el pendiente de §7 |
| `price_alerts`, Telegram, `user_external_accounts` | **No implementado** (la tabla `price_alerts` no está en `SQL/schema.sql`). Verificado, no supuesto |
| `state='owned'` / `'subscription'` | **Implementado**: los tres valores canónicos viven en `LibraryStates` y el binding trata `subscription` como estado propio, nunca como búsqueda de precio |
| UI de biblioteca | **Implementado** → `PLAN_LIBRARY.md`. Esta fila lo daba por pendiente y ya no lo estaba |

El SteamID64 vive en `users.steam_id64` (columna de BD, administrada por el admin y validada en
`UserService.ValidateSteamId64OrThrow`), **no** en una variable de configuración como afirmaba este plan.

## 3. Biblioteca poseída: movida a `PLAN_LIBRARY.md`

Este plan decidía antes que la biblioteca se cruzaba por UUID de ITAD y se importaba de Playnite. Ese
análisis —evidencia verificada, tratamiento por tienda, import, binding y UI— vive ahora en
`PLAN_LIBRARY.md`.

Lo único que queda aquí es el consumo: **las alertas excluyen lo que ya se posee** (§7). El detalle de cómo
los juegos de Amazon se ligan por identidad de Steam en lugar de buscarse en ITAD, por qué Game Pass no
entra en precios y qué pasa cuando un título no liga está en `PLAN_LIBRARY.md` §2, §6 y §7.

## 4. Esquema: qué cambia

Lo que este plan proponía **ya está aplicado**:
`SQL/migrations/2026-09-24_wishlist_and_steam_id.sql`, con su bloque en `SQL/schema.sql`.

Decisiones de esquema que siguen vigentes y que `PLAN_LIBRARY.md` hereda sin cambios:

- `store` es texto libre validado en el servicio, **no** un enum de base de datos: añadir una tienda no
  debe requerir migración. Eso es lo que abarata el `state='subscription'` de Game Pass.
- `state` está dentro de `uq_user_library`, así que un juego deseado, poseído y en Game Pass son **tres
  filas** que coexisten sin colisión.
- `itad_game_id` puede ser `NULL`: significa "identidad no resuelta", no "no lo tienes".

El único DDL nuevo del conjunto es el del catálogo canónico (`PLAN_CATALOG.md` §2): las tablas `games` y
`game_external_ids` y un `game_id` nullable en `steam_games` y en `user_library`. La biblioteca no añade
esquema: liga precios con columnas que ya existen.

## 5. Import de Playnite: movido a `PLAN_LIBRARY.md`

El contrato del export, el endpoint `POST /api/library/import` y los dos puntos pendientes de verificar
(`PluginId` es un GUID, no un nombre de tienda; los nombres exactos que emite Playnite) están en
`PLAN_LIBRARY.md` §6.

La resolución de identidad con ITAD quedó **diferida**, no descartada: `PLAN_LIBRARY.md` §10. En esta fase
el binding no la necesita, porque el resolver de `PLAN_CATALOG.md` §4 sí tiene el id por tienda que da
Playnite.

## 6. Fase 6: wishlist de Steam

| Aspecto | Detalle |
|---|---|
| Endpoint | `GET https://api.steampowered.com/IWishlistService/GetWishlist/v1?steamid=<steamid64>` |
| Key | **No requiere** |
| Requisitos | `steamid64` (no vale la URL vanity); perfil **y wishlist públicos** |
| Devuelve | appid, prioridad, fecha de alta. **No da precios.** |
| Confiabilidad | Registrado en la API de Valve (`GetSupportedAPIList`) pero **sin documentación pública**: `partner.steamgames.com/doc/webapi/IWishlistService` redirige a `/doc` |
| Trampa | Wishlist privada o vacía → `{"response":{}}`. **Indistinguible de "sin juegos".** Hay que mostrar el estado explícito, no "0 juegos". |
| Descartado | `store.steampowered.com/wishlist/profiles/<id>/wishlistdata` redirige roto sin cookies de sesión |

Diseño (ya implementado en el checkout):

- El SteamID64 sale de `users.steam_id64`, una columna administrada por el admin. No hay tabla de cuentas
  externas ni valor en configuración.
- Se guarda como `store_game_id` con `store = 'steam'` y `state = 'wished'`, con la prioridad de Steam.
- Los appids nuevos se insertan en `steam_games` si no existen, para que el detalle y las alertas funcionen
  igual.
- `steam_games.itad_game_id` se resuelve con el mismo `lookup` keyless, en lote.

## 7. Fase 7: alertas y Telegram

- Tabla `price_alerts`: `user_id`, `steam_game_id`, `target_price_minor`, `currency`, `max_price_type`
  (`current` | `history_low`), `is_active`, `last_notified_at`.
- `Deals.API/HostedServices/PriceAlertScheduler.cs` (`BackgroundService`), junto a `FxRateRefreshJob` y
  siguiendo su misma estructura (delay de arranque, `CreateScope` por ciclo, catch que no tumba el proceso).
- Corrida: juegos de `user_library` con `state = 'wished'` + `price_alerts` activas → agrupar por moneda →
  `prices/v3` en lotes de hasta **200 UUIDs por llamada**, pasando por `ProviderRequestGovernor`.
- Consultar el precio con `IFxRateService.GetLatestRateAsync`, **nunca** `GetRateAsync`: el contrato es que
  los requests de usuario y los jobs periódicos no disparan fetch de FX.
- **Solo se alerta de lo deseado: la regla es una allowlist positiva `state = 'wished'`, no una lista negra
  de `owned`.** Con lista negra, `subscription` (Game Pass) se colaría en las alertas. `PriceAlertScheduler`
  todavía no existe, así que la regla se hornea al construirlo y **no hay código que cambiar**. Game Pass no
  es posesión ni compra y nunca alerta.

### `historyLow`: resuelto

Este plan dejaba abierto si persistir el mínimo histórico o eliminarlo. **Ya está resuelto a favor de
persistirlo**: `game_offers.history_low_all_minor` y `history_low_currency` existen (genéricas, no de un
proveedor concreto), las alimenta ITAD desde `historyLow.all` y gg.deals desde `historicalRetail` /
`historicalKeyshops`, y `PLAN_GGDEALS.md` §5 cierra el pendiente equivalente de `PLAN_ITAD.md` §3.2.

Por lo tanto `price_alerts.max_price_type = 'history_low'` **es viable sin migración adicional** y sin
llamadas extra. Queda decidir solo el detalle de producto: si `history_low` compara contra el mínimo de la
tienda concreta o el mínimo global de la oferta.

- Telegram: copiar de `/home/jams45072/Proyectos/Gastos`:
  - `GastosApp.API/Configuration/TelegramOptions.cs` → `Deals.API/TelegramOptions.cs`. **Simplificado**:
    DealExt solo envía, así que basta `Enabled`, `BotToken`, `ChatId`. No hacen falta `AllowedUserId` ni
    `AppUserId`.
  - `GastosApp.API/Extensions/TelegramConfigurationExtensions.cs` → bind + `ValidateOnStart` condicionado a
    `Enabled` + guard de placeholders `SET_`.
  - `GastosApp.API/Services/Telegram/TelegramBotClientProvider.cs` → tal cual: cliente lazy, truncado a
    4000 caracteres, nunca loguea el token.
  - `TelegramWebhookController.cs` → **no se copia**. No se exponen webhooks.
- Nunca notificar dos veces la misma oferta: comparar contra `game_offers` previo y `last_notified_at`.

## 8. Fase 8: UI

- Página `/wishlist` (ya implementada): lista de deseados con estado explícito de la sincronización, y
  **sin** las filas que la biblioteca confirme como poseídas.
- Badge de propiedad en `Deals.Web/app/games/[steamAppId]/game-client.tsx`: lo define `PLAN_LIBRARY.md` §8.
- La página `/library` de juegos comprados es de `PLAN_LIBRARY.md`, no de este plan.
- Nunca mostrar el JSON crudo ni rutas del sistema de archivos del usuario.
- Actualizar `Deals.Web/DESIGN.md` como parte del cambio.

## 9. Orden de fases

```
PLAN_CATALOG.md
  Fase 1  games + game_external_ids + backfill
  Fase 2  resolver en los sitios de inserción

PLAN_LIBRARY.md   (objetivo de la fase actual)
  Fase 0  Game Pass como state='subscription'
  Fase 1  import de Playnite → crear la biblioteca
  Fase 2  binding con precios + nota de "sin binding"
  Fase 3  UI /library + badges
  Fase 4  reseñas por plataforma

Este plan
  Fase 6  wishlist de Steam     ← implementada y commiteada
     └─ Fase 7  alertas + Telegram   ← requiere Fase 6 y el import de PLAN_LIBRARY.md §6
          └─ Fase 8  ajustes de UI de wishlist (excluir poseídos)
```

La numeración se conserva respecto a la versión anterior a propósito: `PLAN_TELEGRAM.md` cita
`PLAN_WISHLIST.md` §7 para la tabla `price_alerts`, y renumberar habría roto esa referencia.

El orden ya está cumplido: ITAD y FX se commitearon antes (`865ec95`), para no perder el orden de las
fechas en `SQL/migrations/`. La decisión de Fase 0 sobre `classification = "authorized"` **ya está cerrada**
(§`PLAN_ITAD.md` 3.1): se eliminó el ternario y el campo `IsOfficial` de la ruta de ITAD, la constante
`authorized` se conserva porque el agregado de gg.deals la usa de verdad, y el parámetro `shops=` se queda
como está. Abrir a tiendas autorizadas sigue costando lo mismo —quitar `shops=` y volver a decidir la etiqueta
en `ApplyItadDeal`—, pero no está pedido.

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| Wishlist de Steam privada devuelve `{}` | Distinguir explícitamente "privada o vacía" en la UI (`WishlistStates`) |
| API de wishlist sin documentación pública ni límites publicados | Aislada tras `ISteamWishlistClient`; ante 429 o cambio de forma, degradar sin romper el resto |
| Cuota ITAD consumida por polling | Lotes, intervalo en horas, TTL, respetar `Retry-After` |
| Notificar dos veces la misma oferta | Comparar contra `game_offers` previo y `last_notified_at` |
| Alertar de un juego ya poseído o de uno de Game Pass | Allowlist `state='wished'`; ni `owned` ni `subscription` entran |
| Fusión por título da falsos positivos (ediciones, remasters) | Detalle en `PLAN_CATALOG.md` §4: sin auto-fusión en V1 |

Los riesgos propios de la biblioteca (export de Playnite, `PluginId`, títulos sin binding, reseñas) están
en `PLAN_LIBRARY.md` §12.

## 11. Fuera de alcance

- OAuth con ITAD y su waitlist.
- Registro público de usuarios o multiusuario.
- **Re-anclar `game_offers` a `game_id`** y renombrar `steam_games`: la receta futura está en
  `PLAN_CATALOG.md` §3. La capa canónica en sí **ya no está fuera de alcance**: la construye
  `PLAN_CATALOG.md`, y este plan la consume.
- **Toda la biblioteca de juegos comprados**: import, binding, página `/library` y reseñas →
  `PLAN_LIBRARY.md`.
- Cálculo de bundles según juegos poseídos: es la integración futura wishlist↔bundles, documentada en
  `PLAN_BUNDLES.md` (fuera de V1 de bundles).
- Sincronización automática del export de Playnite (manual y semanal por decisión).
- Keyshops y grey market como proveedores propios de wishlist/alertas: no se añaden aquí. El comparador
  actual **sí** incluye el agregado de keyshops de gg.deals en el detalle del juego, pero eso no se extiende
  a la wishlist.

## 12. Verificación

| Fase | Evidencia |
|---|---|
| 6 | Importar una wishlist pública real: el conteo cuadra con Steam; un perfil privado muestra el estado explícito, no "0 juegos" |
| 7 | Una alerta alcanzable produce un único mensaje de Telegram; un juego poseído no genera alerta; un juego de Game Pass tampoco; una alerta por `history_low` compara contra el mínimo persistido |
| 8 | `pnpm build`; la vista de wishlist no muestra un juego confirmado como poseído |

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```

## Anexo: script de export para Playnite

Movido a `PLAN_LIBRARY.md` §Anexo A: el export de Playnite pertenece a la biblioteca, que ya no se
documenta en este plan.
