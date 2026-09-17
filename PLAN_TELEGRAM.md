# DealExt: plan de notificaciones Telegram genéricas multiplataforma

Estado: propuesto, pendiente de aprobación. Solo análisis y plan, sin código.

Alcance: un canal de notificaciones Telegram **genérico y desacoplado** de cualquier proveedor de precios. Cualquier fase o scheduler (ITAD, gg.deals, Steam, futuros) produce eventos de precio; este plan solo captura, deduplica y entrega los mensajes. Nunca consulta APIs de precios: eso es de cada scheduler.

Continúa a `PLAN_ITAD.md`, `PLAN_GGDEALS.md` y `PLAN_WISHLIST.md`. Leer también `AGENTS.md` y `PLAN_BASE_MVP.md`.

## 1. Principio de diseño: separación productor/consumidor

Hoy los planes mezclan dos cosas: obtener precios (ITAD `prices/v3`, gg.deals, Steam) y notificar (Telegram). El problema es que cada scheduler termina copiando el cliente de Telegram y su deduplicación.

Separación:

```
Scheduler ITAD ──┐
Scheduler gg.deals ──┤→ price_alert_events (tabla commun) → PriceAlertDispatcher → Telegram
Scheduler Steam ──┘                                                        (único cliente bot)
```

| Regla | Razón |
|---|---|
| Telegram vive en **un solo lugar** | Un cliente, una configuración, un patrón de retry. Ningún scheduler conoce Telegram. |
| Proveedores escriben **eventos de precio**, no mensajes | El formato del mensaje es responsabilidad del dispatcher, no de quien obtiene el precio. |
| Un evento es **datos**, no HTML | `game_offers` ya tiene precio original, MXN, moneda, DRM, tienda. El dispatcher arma el texto. |
| Una oferta notificada **una sola vez** | Dedup en la tabla de eventos, no en cada scheduler. |

## 2. Lo que ya existe y se reutiliza

| Ya implementado | Uso aquí |
|---|---|
| `Deals.API/HostedServices/FxRateRefreshJob.cs` | Plantilla exacta del dispatcher: `BackgroundService`, delay de arranque, `CreateAsyncScope` por ciclo, catch que no tumba el host. |
| `IFxRateService.GetLatestRateAsync` (solo lectura) | Para notas de conversión en el mensaje. Nunca `GetRateAsync` desde jobs. |
| `game_offers` (snapshot por oferta con `mxn_*`, `classification`, `shop_name`, `deal_url`, `pricing_type`) | Fuente única de datos para armar el mensaje. |
| `steam_games` (nombre, appid) | Título del juego en el mensaje. |
| `/home/jams45072/Proyectos/Gastos` — `TelegramBotClientProvider.cs` | Se copia adaptado: cliente lazy, truncado 4000 caracteres, nunca loguea el token (solo `exception.GetType().Name`). |
| `TelegramOptions` de Gastos | **Simplificado**: solo `Enabled`, `BotToken`, `ChatId`. Sin `AllowedUserId` ni `AppUserId` (DealExt solo envía). |
| `ValidateOnStart` condicional (patrón de `FxOptions`/`ItadOptions`) | Guard de placeholder `SET_` solo si `Enabled=true`. |

**No existe nada de** Telegram, `price_alerts` ni `price_alert_events`. Verificado contra `SQL/schema.sql` y el árbol de archivos.

## 3. Esquema de datos

Una tabla de eventos genérica + la tabla de alertas específicas de wishlist (ya definida en `PLAN_WISHLIST.md` §7).

```sql
-- SQL/migrations/2026-09-XX_price_alert_events.sql (+ bloque en SQL/schema.sql)
CREATE TABLE price_alert_events (
    price_alert_event_id BIGSERIAL PRIMARY KEY,
    source VARCHAR(32) NOT NULL,               -- itad | ggdeals | steam | futuro
    steam_game_id INT NOT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
    shop_name VARCHAR(128),
    currency VARCHAR(3),
    price_minor INT,
    mxn_minor INT,
    trigger_reason VARCHAR(32) NOT NULL,       -- target_reached | history_low | new_deal
    dedup_key VARCHAR(256) NOT NULL,           -- ver §4
    notified_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_price_alert_events UNIQUE (source, dedup_key)
);

CREATE INDEX idx_price_alert_events_pending ON price_alert_events(notified_at) WHERE notified_at IS NULL;
CREATE INDEX idx_price_alert_events_game ON price_alert_events(steam_game_id);
ANALYZE price_alert_events;
```

- `source` es texto libre validado en servicio: agregar gg.deals no requiere migración.
- `dedup_key` lo construye el scheduler productor (ver §4). El dispatcher nunca re-notifica lo ya notificado.
- La tabla `price_alerts` de wishlist (umbral por juego) queda **fuera** de este plan: es dominio de wishlist. Este plan solo entrega lo que otros schedulers encolan.

## 4. Contrato del productor

Cada scheduler de precios llama a un único servicio al detectar algo notificable:

```csharp
public interface IPriceAlertEnqueuer
{
    Task EnqueueAsync(PriceAlertEvent ev, CancellationToken ct);
}

public sealed record PriceAlertEvent(
    string Source,
    int SteamGameId,
    string? ShopName,
    string? Currency,
    int? PriceMinor,
    int? MxnMinor,
    string TriggerReason);
```

Reglas del productor:

1. El productor calcula `dedup_key` = `$"{trigger_reason}:{shopName ?? "direct"}:{priceMinor ?? 0}:{fecha resumida YYYY-MM-DD}"`. Mismo juego + misma tienda + mismo precio el mismo día = una sola fila. Si el precio baja de nuevo, la key cambia → nueva notificación (correcto).
2. El upsert es idempotente: si `(source, dedup_key)` ya existe con `notified_at` no nulo, se descarta en silencio.
3. El productor **no** envía nada a Telegram y no falla si Telegram está deshabilitado.
4. Nunca encola si el precio apto cambia sin cruzar el umbral definido por el productor (cada scheduler define su propia condición: `target_reached` para wishlist, `new_deal` para updates).

## 5. Dispatcher (único consumidor)

`Deals.API/HostedServices/PriceAlertDispatcher.cs` — `BackgroundService`, junto a `FxRateRefreshJob`:

- Mismo patrón: delay de arranque (30 s), intervalo configurable (`Telegram__PollSeconds`, default 60), `CreateAsyncScope` por ciclo, catch que no tumba el host.
- Cada ciclo: `SELECT ... FROM price_alert_events WHERE notified_at IS NULL ORDER BY price_alert_event_id LIMIT 50`.
- Por cada evento arma el mensaje:

```
🏷️ {titulo del juego}
💰 {tienda}: {precio original} {moneda} → ≈ {mxn} MXN
📉 {razon}: {texto segun trigger_reason}
🔗 {deal_url} (si hay)
✨ Datos de precios: IsThereAnyDeal (según source)
```

- Truncado a 4000 caracteres (heredado de Gastos). Nunca loguea el token.
- Envía con `TelegramBotClientProvider.TrySendAsync`; en éxito marca `notified_at = now`. En fallo loguea `exception.GetType().Name` y reintenta en el próximo ciclo (no re-encola).
- Envía **por lotes de máximo 20 mensajes/ciclo** (Telegram: ~30 msg/s por chat; 20 es prudente).
- Si `Telegram:Enabled=false`: no consume la tabla; los eventos quedan pendientes y se procesan al habilitarse. No se lanza error al arrancar.

## 6. Cliente Telegram

Copia adaptada de `GastosApp.API/Services/Telegram/TelegramBotClientProvider.cs`:

- `Deals.API/TelegramOptions.cs` — `SectionName="Telegram"`: `Enabled` (bool, default false), `BotToken`, `ChatId` (`long`).
- `Deals.API/Extensions/TelegramConfigurationExtensions.cs` — bind + `ValidateOnStart` **solo si** `Enabled=true` + guard de placeholders `SET_`.
- `Deals.API/Services/Telegram/TelegramBotClientProvider.cs` — cliente lazy con `HttpClient` tipado, `SendMessage` contra `https://api.telegram.org/bot{token}/sendMessage` (token **solo en path interno, nunca en logs ni errores**), `parse_mode=HTML` opcional, truncado 4000, `DisableNotification` configurable.
- **No se copia** `TelegramWebhookController.cs`. Sin webhooks, sin exponer endpoints. Bot operado en modo *push-only*.
- `TelegramBotClientProvider` es transitorio/scoped según DI existente; el dispatcher lo resuelve por scope.

Configuración (`.env.example` + `docker-compose.yml` api.environment solamente):

```
Telegram__Enabled=false
Telegram__BotToken=SET_TELEGRAM_BOT_TOKEN
Telegram__ChatId=0
Telegram__PollSeconds=60
```

## 7. Fechas copiadas de los schedulers ya definidos

Este plan no define schedulers de precios: los define cada plan de proveedor. Recordatorio de lo ya acordado en `PLAN_GGDEALS.md` §1 y `PLAN_WISHLIST.md` §7:

| Scheduler | Fuente de precio | Umbral notificable |
|---|---|---|
| ITAD (wishlist) | `prices/v3` en lotes de 200 vía `ItadRequestGovernor` | `currentPriceMinor <= target_price_minor` o `historyLow ≤ target` |
| gg.deals | `prices/by-steam-app-id` en lotes de 100 | Baja de `currentRetail` o `currentKeyshop` vs snapshot previo |
| Steam (futuro) | Storefront | `discountPercent > 0` y `is_free` |

Cada uno encola en `price_alert_events` con su `source`; el dispatcher es el único que toca Telegram. Ningún scheduler armará texto HTML propio.

## 8. Fuera de alcance

- Comandos en Telegram (el bot no recibe, solo envía).
- Webhooks de Telegram o de cualquier proveedor.
- Multiusuario: un ChatId único del admin.
- Tabla `price_alerts` de wishlist (dominio de `PLAN_WISHLIST.md` §7).
- Formatos rich (inline keyboards, botones de acción). Solo texto plano con emojis.
- Reintentos backoff exponencial: reintento simple en el próximo ciclo del dispatcher.

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| El token del bot termina en logs | Solo `exception.GetType().Name`; URL del token nunca pasa por `logger` ni por mensajes de error al cliente |
| Un bug del dispatcher reenvía todo | Dedup por `(source, dedup_key)` + `notified_at` solo tras éxito confirmado |
| Telegram cae → cola crece | `LIMIT 50` por ciclo; mensajes antiguos se procesan en orden; la tabla crece y se purga manual o con un job de retención (30 días) |
| Scheduler productor encolo basura (e.g. precio相同的 repetido) | `dedup_key` con fecha resumida: mismo día no repite |
| Rate limit Telegram (~30 msg/s) | 20 mensajes/ciclo + intervalo configurable ≥ 60 s |
| `ChatId` mal configurado | Mensaje de prueba manual al habilitar (procedimiento operativo, no endpoint) |

## 10. Fases

| Fase | Contenido |
|---|---|
| 0 | Aplicar migración `price_alert_events`; subir `Telegram__*` al entorno; copiar/adaptar `TelegramOptions` + provider de Gastos |
| 1 | `IPriceAlertEnqueuer` + tabla;调度ador de wishlist encola su primer evento real |
| 2 | Dispatcher lee pendientes y envía; dedup verificado; un mensaje por alerta y no repetido en el siguiente ciclo |
| 3 | gg.deals encola sus eventos cuando se implemente `PLAN_GGDEALS.md` fase 2; mismo dispatcher sin cambios |

## 11. Verificación

| Fase | Evidencia |
|---|---|
| 0 | `dotnet build Deals.sln` verde; evento de prueba en `price_alert_events` con `notified_at IS NULL` |
| 1 | Una alerta de wishlist cruza umbral → aparece 1 fila en `price_alert_events` |
| 2 | Dispatcher corre → se envía 1 mensaje a Telegram; `notified_at` queda marcado; el siguiente ciclo no reenvía |
| 3 | Scheduler gg.deals encola → mismo flujo, distinto `source`, sin tocar el dispatcher |

Comandos:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```

## 12. Fuera de alcance de este documento

- La lógica de cada scheduler de precios (ver `PLAN_WISHLIST.md` §7 y `PLAN_GGDEALS.md`).
- La tabla `price_alerts` de wishlist (ver `PLAN_WISHLIST.md` §7).
- gg.deals como proveedor (ver `PLAN_GGDEALS.md`).
