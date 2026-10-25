# Alertas de ofertas por ntfy

Estado: implementado (T3 de `bundles-ntfy`), deshabilitado por defecto. Cubre S2-S5.

Alcance: un canal **global** de avisos de oferta de **juegos** hacia un topic de ntfy. Consume únicamente
snapshots **ya persistidos y frescos** de `game_offers`; nunca llama a un proveedor de precios, nunca
recalcula identidad y nunca toca los bundles. La comparación de tiers de un bundle es otro dominio
(`docs/PLAN_BUNDLES.md`): un bundle no es una oferta por juego y no entra aquí.

## 1. Reglas de disparo (S2, S4)

Un candidato es **una fila fresca de `game_offers`**. Dispara cuando **cualquiera** de estas tres reglas
independientes es cierta:

| Regla | Condición exacta | Constante |
|---|---|---|
| Nota | `score > 7` (estricto; 7.0 **no** dispara) | `DealAlertPolicy.ScoreTriggerThreshold` |
| Precio | `mxn_current_price_minor < 5000` (estricto; 5000 = MX$50.00 **no** dispara) | `DealAlertPolicy.CheapPriceThresholdMinor` |
| Mínimo histórico | mínimo de proveedor verificado (ver §2) | `DealAlertPolicy.HasVerifiedHistoryLow` |

El `score` es el puerto exacto de la fórmula 0-10 de la wishlist
(`Deals.Web/app/wishlist/_lib/wishlist-metrics.ts`): 7 puntos por la escala de descuento contra el umbral
viable y 3 por cercanía al mínimo histórico. El umbral viable por defecto es 50 y es configurable
(`Ntfy__MinViableDiscountPercent`). La comprobación de consistencia vive en `checks/Ntfy.Checks`.

Las tres reglas son también las tres pestañas de `/discover` (`under50`, `historic`, `score`), leídas de
los mismos snapshots: un aviso y una tarjeta no pueden discrepar sobre qué es «menos de 50», qué es un
mínimo verificado ni qué nota supera el umbral.

La nota **no** es calidad del juego: es calidad del precio.

## 2. Evidencia de mínimo histórico

`HasVerifiedHistoryLow` exige, todo a la vez:

1. El mínimo viene de la **misma fila de oferta** que el precio actual (misma fuente por construcción).
2. La moneda del mínimo es **igual** a `original_currency` de la oferta: nada de conversión FX ni de
   comparar pesos con dólares.
3. El precio original actual es `> 0` y `<=` el mínimo.

Nunca cuenta como evidencia:

- un mínimo ausente (`history_low_all_minor` NULL) o en cero;
- el mínimo local de Steam (`steam_games.lowest_price_minor`): lo crean nuestras propias observaciones y
  en la primera observación `current == lowest` sería un falso positivo;
- una fila `source = 'steam'`, que por definición no trae mínimo de proveedor.

## 3. Exclusiones (`IsComparable`)

No son comparables y no disparan:

- región distinta de `mx` exacto en minúsculas, ausente o con espacios: convertir el importe a MXN no convierte una oferta extranjera en una oferta de México;
- precio MXN nulo, cero, negativo o por encima de `MaxPriceMinor` (dato corrupto);
- `pricing_type = 'unconverted'` (un monto sin convertir no es un precio MXN);
- filas de **Epic** gratis o con precio original dudoso (`S5: epic pendiente`);
- snapshots fuera de la ventana de frescura (`Ntfy__MaxSnapshotAgeHours`, 72 h por defecto) o con fecha
  futura.

## 4. Deduplicación y reintentos

Tabla `deal_alert_events` (migración `SQL/migrations/2026-10-17_ntfy_deal_alerts.sql`, idempotente):

- `dedup_key` único = `v{trigger_version}|{game}|{source}|{offer_key}|{price_minor}`.
- Misma oferta + mismo precio + misma versión de política = **una sola fila**. Un job diario no puede
  repetir la misma oferta sin fin; un precio distinto sí vuelve a avisar.
- `status`: `pending | sent | failed | stale`.
- `attempts` y `next_attempt_at` dan el backoff exponencial acotado (`NtfyRetrySchedule`),
  `Ntfy__MaxAttempts` lo corta y `last_error` guarda **solo** el tipo de resultado o el status HTTP.

Antes de enviar, el dispatcher **revalida** la fila contra el snapshot vivo: si la oferta desapareció, cambió
de precio o dejó de disparar, la fila pasa a `stale` y no se envía.

## 5. Flujo y concurrencia

`NtfyDealsJob` (`BackgroundService`) hace un ciclo cada `Ntfy__PollSeconds`:

1. Toma un advisory lock de PostgreSQL (`7_831_442_092`, distinto del de wishlist) para serializar entre
   procesos.
2. **Productor**: lee `game_offers` frescos, aplica `DealAlertPolicy` y encola solo lo que dispara.
3. **Dispatcher**: reclama pendientes vencidos (tope `Ntfy__DispatchBatchSize`), revalida, envía y marca.

Los rechazados se descartan **antes** de tocar el outbox: un rechazo no muta ninguna fila existente.

## 6. Cliente ntfy y seguridad

`NtfyAlertSender` hace un único `POST` con `Bearer` al topic configurado:

- solo el **status HTTP** decide el resultado; el cuerpo de la respuesta no se lee ni se registra;
- el token, la URL completa y el topic **nunca** aparecen en logs ni en mensajes de error;
- un **timeout** devuelve `Ambiguous`: el envío es *at-least-once*, no *exactly-once*, porque no hay forma
  de saber si el mensaje salió. Se reintenta con backoff.

## 7. Configuración

```
Ntfy__Enabled=false
Ntfy__BaseUrl=https://ntfy.amitzi.xyz/
Ntfy__Topic=Deals
Ntfy__AuthToken=SET_NTFY_AUTH_TOKEN
# Opcionales (defaults en código): PollSeconds=900, StartupDelaySeconds=60, RequestTimeoutSeconds=10,
# MaxSnapshotAgeHours=72, MinViableDiscountPercent=50, DispatchBatchSize=20, MaxAttempts=5,
# RetryBaseSeconds=60, RetryMaxSeconds=3600, MinSecondsBetweenSends=1
```

Con `Enabled=false` el job sale al arrancar, la validación de opciones es condicional y no se inserta ni
se envía nada.

## 8. Verificación

```
dotnet run --project checks/Ntfy.Checks
dotnet build Deals.sln
```

`checks/Ntfy.Checks` cubre: la fórmula previa de la wishlist, los límites estrictos 5000/7, las tres reglas
OR, evidencia de mínimo histórico (moneda/fuente), entradas inválidas/stale/Epic, identidad de dedup, el
contrato de "rechazado no toca el outbox" y el sender HTTP con handler falso. **No envía mensajes reales.**

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un bug reenvía todo | Índice único en `dedup_key` + `status` solo se marca `sent` con 2xx |
| Timeout → mensaje duplicado | `Ambiguous` es at-least-once explícito; el dedup evita reencolar el mismo precio |
| Token/topic en logs | Solo status y `exception.GetType().Name`; el cuerpo nunca se lee |
| Cola creciente | `DispatchBatchSize` por ciclo + `failed`/`stale` retiran filas |
| Precio viejo | Revalidación previa al envío + `MaxSnapshotAgeHours` |
| Dos hosts al mismo tiempo | Advisory lock de sesión PostgreSQL |

## 10. Mapeo de specs

| Spec | Cobertura |
|---|---|
| S2 | Reglas de nota/precio/mínimo + job + sender |
| S3 | `https://ntfy.amitzi.xyz/`, topic `Deals`, `Bearer` por configuración |
| S4 | Fórmula exacta 0-10 + combinación OR `nota o 50 o mínimo` |
| S5 | Epic gratis/dudoso excluido |
