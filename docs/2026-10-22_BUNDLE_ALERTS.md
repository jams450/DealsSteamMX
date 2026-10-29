# Recordatorio rojo de bundles (spec S2)

Aviso por `ntfy` cuando un bundle pendiente está por vencer: **todos** los bundles pendientes, sin wishlist ni
favoritos, un solo aviso por bundle y ciclo de vencimiento, y **nada** si el bundle ya se compró. La etiqueta de
color de la UI la define [`bundle-expiry.ts`](../Deals.Web/app/bundles/_lib/bundle-expiry.ts) (spec S1); este
documento cubre solo el aviso.

Contexto del canal y sus puertas actuales: [`NTFY_DEAL_ALERTS.md`](NTFY_DEAL_ALERTS.md). Nada de este documento
—ni el código— contiene un tema, un token ni una URL real.

## Regla

| Regla | Definición | Borde |
|---|---|---|
| Ventana roja | `expires_at > ahora Y expires_at <= ahora + 72h` | exactamente 72 h **sí** avisa; 72 h + 1 s **no** |
| Días del texto | `ceil((expires_at - ahora) / 24h)`, el mismo cálculo que la etiqueta | 23 h = «1 día»; 72 h = «3 días» |
| Sin fecha | `external_bundles.expires_at IS NULL` | nunca avisa: no hay ciclo que contar |
| Expirado | `expires_at <= ahora` | nunca avisa: ya no está activo |
| Comprado | existe fila en `bundle_purchases` del operador | se suprime, no se envía |
| Dedup | operador + **familia de fuente** + `bundle_key` + instante de vencimiento | repetir el job no duplica |
| Familia | `itad` e `itad_global` son la misma; `ggdeals` es suya | el título nunca es identidad |

La ventana y los días se aplican en `BundleAlertPolicy` (puro, sin reloj propio), y se reaplican en el productor
y en el despachador: la consulta SQL acota la lectura, pero la regla no vive solo en SQL.

## Puertas, en orden

| Puerta | Dónde | Si no se cumple |
|---|---|---|
| `Ntfy__Enabled=false` | job (no arranca) + productor y despachador (segunda puerta) | no lee, no encola, no reclama, no envía |
| Operador configurado y activo (`Ntfy__UserId`, default 1) | `IDealAlertRecipientScope.LoadAsync` | falla cerrado: productor y despachador no hacen nada |
| Ventana roja | productor (en memoria, además del SQL) | el candidato no se encola |
| Ya comprado | `IBundlePurchaseGate` en el **productor** | no se encola (`suppressed` en el contador) |
| Fila de otra cuenta | despachador, por fila | `stale`, sin intento |
| Ya comprado | `IBundlePurchaseGate` en el **despachador**, antes del HTTP | `suppressed`, sin envío y sin gastar intento |
| Vencimiento vigente | `IBundleAlertSource.FindCurrentAsync`: misma fecha exacta y sigue en ventana | `stale`: la fecha del texto ya sería falsa |
| Operador vigente | relectura del alcance justo antes del envío | `stale` |

El alcance del operador es el mismo objeto que usan las alertas de oferta, pero aquí **solo** se usa su
`UserId`: los conjuntos de favoritos y wishlist se ignoran a propósito. Esa es la única diferencia con la puerta
de ofertas, y el conjunto de ofertas quedó intacto (lo prueba `checks/BundleAlerts.Checks` sobre el mismo ciclo).

## Compra antes del envío

El productor consulta `bundle_purchases` antes de encolar y el despachador **vuelve a consultar antes del HTTP**.
La consulta se acota en SQL por `user_id` y `bundle_key`, y la familia se decide con
`BundlePurchaseRules.HidesBundle` — la misma función que oculta el bundle comprado del catálogo pendiente, así
que «la compra oculta la tarjeta» y «la compra silencia el aviso» no pueden divergir. Nunca hay supresión cruzada:
la compra de otra cuenta no apaga este aviso (la clave de dedup lleva el `user_id`).

**Frontera aceptada.** Una compra puede confirmarse entre la relectura y el POST a ntfy, o durante él. No se
mantiene ningún lock a través de esa llamada: el POST es externo y sin cota, y sostener una transacción o el lock
global de identidad sobre él convertiría un tema lento en un pipeline detenido. La ventana es real y se documenta:
como mucho un mensaje sobre un bundle recién comprado, y la fila no se reintenta porque el envío ya se respondió.
Todo lo demás —compra anterior a la puerta— se suprime.

## Esquema

Tabla propia `bundle_alert_events` (`SQL/migrations/2026-10-22_bundle_alert_events.sql`), **no** una extensión de
`deal_alert_events`: un bundle no tiene precio ni identidad de juego, y compartir la tabla habría obligado a
compartir el dedup y las puertas de interés de las ofertas.

- `dedup_key` `VARCHAR(320)` **UNIQUE**: `bundle-alert:v1:<user_id>:<source_family>:<bundle_key>:<expires_at µs UTC>`.
- `expires_at`: **snapshot** por el que se encoló; se revalida contra `external_bundles` antes de cada envío.
- `status`: `pending | sent | failed | stale | suppressed` (CHECK).
- `click_url`: `NULL` o `https://…` (CHECK); el cliente ntfy aplica la misma regla otra vez.
- `last_error`: solo el nombre del resultado (`Delivered`/`Retryable`/`Permanent`/`Ambiguous`), nunca URL, tema,
  token ni cuerpo de excepción.
- Índices: `(status, next_attempt_at)` para el barrido, `(user_id, bundle_key)` para auditoría. FK a `users` con
  `ON DELETE CASCADE`.
- Invariantes de solo lectura: `SQL/checks/bundle_alert_events.sql` (cada consulta devuelve 0 filas).

## Despliegue

1. Aplicar a mano `SQL/migrations/2026-10-22_bundle_alert_events.sql` (no hay workflow de migraciones EF). El
   slice de compras hace lo mismo con la suya, `2026-10-21_bundle_purchases.sql`; el aviso consulta esa tabla, así
   que las dos migraciones van juntas.
2. Composición pendiente del dueño del bootstrap (este slice no toca `AppDbContext`, `SQL/schema.sql` ni
   `ServiceCollectionExtensions`). Ver la sección «Composición» al final.
3. Correr `SQL/checks/bundle_alert_events.sql`: 0 filas en cada consulta.
4. Verificar sin enviar nada: `dotnet run --project checks/BundleAlerts.Checks`.

Las fases de bundle están aisladas dentro del ciclo: si su tabla todavía no existe, el error queda registrado en
`job_runs.details.bundles.produceError` / `dispatchError` **solo como nombre de tipo** y las alertas de oferta
siguen funcionando. Un ciclo con una fase de bundle fallida se registra como fallido.

## Ciclo y detalle de `job_runs`

`ntfy.alerts` sigue siendo un solo job, con las mismas opciones (`produce`, `dispatch`) y el mismo lock de
asesoría (`NtfyDealsJobExecutor.AdvisoryLockKey`), compartido por el job programado y el despacho manual del
admin. Los contadores de oferta conservan sus nombres planos; los de bundle van en `details.bundles`:

```
produced, dispatched, candidates, triggered, enqueued, suppressed,
claimed, sent, retried, failed, stale, suppressedAtDispatch, produceError, dispatchError
```

## Verificación

| Comando | Qué prueba |
|---|---|
| `dotnet run --project checks/BundleAlerts.Checks` | 72 h exactas sí / 73 h no, sin fecha y expirado no, dedup idempotente por familia, `ggdeals` separado, nunca por título, sin wishlist, fallo cerrado sin operador, compra antes de encolar y antes de enviar, fecha movida = `stale`, URL inválida saneada, reintento/backoff y entrega con cliente HTTP falso, puerta de ofertas intacta, integración real en el ciclo |
| `dotnet run --project checks/Ntfy.Checks` | las puertas de las alertas de oferta siguen igual (PRESERVE) |
| `dotnet run --project checks/Jobs.Checks` | el catálogo, las opciones y el contrato de detalles del job |
| `dotnet build Deals.sln` | el backend compila con la entidad nueva |

Ningún check abre una conexión, aplica una migración ni envía un mensaje real.

## Limitaciones conocidas

- **Sin verificación contra PostgreSQL real en este slice:** el índice único bajo dos productores concurrentes, el
  FK de `users` y el `CHECK` del estado solo se comprueban aplicando la migración y
  `SQL/checks/bundle_alert_events.sql`. El lock de asesoría ya serializa los ciclos, así que la carrera que queda
  es la de la frontera externa descrita arriba.
- `SQL/schema.sql` (fuente de verdad, propiedad del slice de escritura del backend) todavía no tiene la tabla;
  hasta que se añada, una base creada desde cero no la tendrá.
- El aviso no incluye el precio del tier: `external_bundles.tiers_json` es una proyección de display y el aviso no
  lo interpreta.
- Un aviso ya enviado no se «desenvía» si el bundle se compra después: el estado `sent` es historia, no cola.

## Composición (pendiente del dueño del bootstrap)

Este slice no edita `AppDbContext`, `SQL/schema.sql` ni `ServiceCollectionExtensions`; los toca el dueño del
bootstrap. Son exactamente tres cambios, y sin el primero el outbox no se puede usar (EF no descubre la entidad):

**1. `Deals.BusinessLogic/Context/AppDbContext.cs`** — una línea junto a los demás `DbSet`. No hace falta configuración
en `OnModelCreating`: la tabla, la PK y las columnas vienen de los atributos de `BundleAlertEvent`, y el índice
único, los `CHECK` y los índices de barrido viven en la migración manual.

```csharp
public DbSet<BundleAlertEvent> BundleAlertEvents => Set<BundleAlertEvent>();
```

**2. `Deals.API/Program.cs`** — una línea al final de la cadena fluida, después de `AddApiNtfyNotifications()`:

```csharp
    .AddApiApplicationServices()
    .AddApiNtfyNotifications()
    .AddApiBundleAlerts();   // Deals.API.Services.Notifications.BundleAlertServiceCollectionExtensions
```

Registra `IBundleAlertOutbox`, `IBundleAlertSource`, `IBundlePurchaseGate`, `BundleAlertProducer` y
`BundleAlertDispatcher` (todos scoped, `TryAdd`). No re-registra el sender ni el alcance del operador: reusa los de
`AddApiNtfyNotifications()`. Sin esta línea, `NtfyDealsJobExecutor` no se puede construir (dependencia obligatoria).

**3. `SQL/schema.sql`** — la misma DDL que `SQL/migrations/2026-10-22_bundle_alert_events.sql`, para que una base
creada desde cero tenga la tabla.

Nada más: el cableado del ciclo (`Deals.API/Services/Jobs/JobExecutors.cs`) ya está hecho en este slice.
