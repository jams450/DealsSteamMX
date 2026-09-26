# Plan: Jobs / Log como centro de control

## Objetivo

Crear una sección administrativa de **Jobs / Log** que permita comprobar qué sincronizaciones han corrido, cuándo, con qué resultado y cuánto trabajo han procesado. La primera versión debe convertir el historial existente en una fuente operativa fiable sin introducir todavía Hangfire ni una cola distribuida.

La sección no será un visor indiscriminado de logs de aplicación. Será un panel basado en ejecuciones estructuradas (`job_runs`), con errores sanitizados y métricas agregadas por job, fase y proveedor.

## Decisión propuesta

| Tema | Propuesta inicial | Motivo |
|---|---|---|
| Motor | `BackgroundService` + PostgreSQL, evolucionando el scheduler actual | Ya existen `WishlistSyncJob`, `FxRateRefreshJob` y `job_runs`; hay pocos jobs y no se ha demostrado la necesidad de una plataforma externa |
| Hangfire | Diferido | No resuelve por sí mismo locks de negocio, métricas de proveedor ni clasificación de rate limits |
| Acceso | Solo administrador | El panel expone salud operativa, proveedores y errores; no es una función de usuario final |
| Historial | Un registro por ejecución, con fases y métricas estructuradas | Permite distinguir ejecución completa, parcial, degradada y fallida |
| Rate limits | Medidos desde respuestas reales de cada cliente, además de un presupuesto configurable por proveedor | El límite de API protege endpoints; no describe el consumo frente a Steam, ITAD, gg.deals, Epic o Microsoft |
| Configuración de límites | Variables de entorno para valores base y panel admin para overrides operativos | Permite ajustar pacing sin recompilar; un cambio de entorno requiere reinicio |
| Ejecución manual | Asíncrona: iniciar devuelve `jobRunId`; la UI consulta estado | Evita mantener una petición abierta durante refreshes largos |
| Cancelación | Cooperativa, posterior al modelo base de ejecución | Requiere que cada fase y cliente respete `CancellationToken` |
| Retención | Definida por política antes de migrar | Evita que `job_runs` crezca indefinidamente |

## Auditoría de la situación actual

### Ya existe

- Entidad y tabla `JobRun` / `job_runs`.
- Estados `running`, `ok` y `failed`.
- `JobRunLog.StartAsync`, `FinishAsync` y `RanWithinAsync`.
- `WishlistSyncJob` como `BackgroundService` con configuración de horario, jitter, recuperación y timeout.
- `FxRateRefreshJob` como `BackgroundService`, pero sin historial persistido en `job_runs`.
- Registro manual de `POST /api/wishlist/sync`.
- Pacing y tratamiento de respuestas limitadas en clientes de proveedores.
- Timestamps de refresh independientes en el dominio de precios.

### Ausencias relevantes

- Endpoint administrativo y BFF para consultar ejecuciones.
- Página Jobs / Log y contrato frontend.
- Registro homogéneo para FX, biblioteca y fases de proveedores.
- Conteos comunes de `processed`, `succeeded`, `failed`, `skipped` y `rateLimited`.
- Duración, intento, error estructurado, `nextRetryAt` y estado de cancelación.
- Fases/proveedores como hijos o eventos de una ejecución.
- Claim/lease atómico para impedir dos ejecuciones concurrentes.
- Purga/retención.
- Reintento y cancelación controlados desde UI.

## Modelo funcional propuesto

### Jobs iniciales

| Job | Fases observables | Estado actual |
|---|---|---|
| `wishlist.sync` | importar lista, refresh de juegos/precios | Ya usa `job_runs`; hay que normalizar métricas |
| `fx.refresh` | consulta y persistencia de FX | Debe empezar a registrar ejecuciones |
| `library.store-prices` | selección de candidatos, Microsoft/Xbox y escritura de offers | El servicio devuelve conteos; falta un run común |
| `library.covers` | sincronización de covers | Incorporar solo si se ejecuta como operación background; no inventar métricas para una petición síncrona |
| futuras sincronizaciones | una clave estable por job y fases versionadas | Deben reutilizar el contrato común |

Steam, ITAD, gg.deals, Epic y Microsoft no deben convertirse automáticamente en cinco jobs independientes. En V1 serán **proveedores/fases** dentro del job que los invoque. Se separarán como jobs solo si necesitan scheduling, límites o reintentos independientes.

### Estados

Conservar `running`, `ok` y `failed` como estados base y añadir, si la implementación lo confirma, estados operativos explícitos:

- `partial`: terminó con elementos procesados y fallos recuperables.
- `rate_limited`: el proveedor bloqueó o aplazó el trabajo.
- `cancel_requested` / `cancelled`: cancelación cooperativa.
- `abandoned`: lease expirado o proceso muerto, diferenciado de un job todavía activo.

No marcar como `ok` una ejecución que terminó sin error de proceso pero dejó fallos de proveedor sin reflejar.

### Métricas mínimas por ejecución

- `job`, `trigger`, `status`.
- `startedAt`, `finishedAt`, duración calculable.
- `attempt`.
- `processed`, `succeeded`, `failed`, `skipped`.
- `rateLimited`, `retryCount`.
- fase/proveedor y su resultado.
- `lastErrorCode`, mensaje sanitizado y `retryAfterSeconds` cuando exista.
- `detailsVersion` para versionar el JSON adicional.
- `requestedByUserId` solo para ejecuciones manuales, si se decide conservar trazabilidad.

El panel debe mostrar también el **último resultado por job/proveedor** y una ventana agregada (por ejemplo, últimas 24 horas o últimas N ejecuciones), sin recalcular estadísticas costosas en cada render.

## Presupuesto configurable y rate limits reales

Sí conviene declarar un presupuesto operativo configurable por proveedor en la configuración, pero hay que separar dos conceptos:

1. **Límite oficial del proveedor:** la cuota real impuesta por ITAD o gg.deals. No debe inventarse ni asumirse como `N solicitudes por hora` si la documentación o la cuenta no lo confirma.
2. **Presupuesto local de DealExt:** el máximo que nuestra aplicación decide consumir, normalmente más conservador que la cuota oficial.

La configuración propuesta es por proveedor y por ventana:

```text
Providers__Itad__RequestsPerMinute
Providers__Itad__RequestsPerHour
Providers__Itad__RequestsPerDay
Providers__GgDeals__RequestsPerMinute
Providers__GgDeals__RequestsPerHour
Providers__GgDeals__RequestsPerDay
```

Los valores deben ser opcionales. Un valor ausente significa que no se aplica esa ventana local, no que el proveedor permita ilimitado. Conviene añadir también:

```text
Providers__Itad__Burst
Providers__Itad__MinDelayMilliseconds
Providers__GgDeals__Burst
Providers__GgDeals__MinDelayMilliseconds
```

El cliente debe leer estas opciones mediante `IOptionsMonitor` o una abstracción equivalente, para que la UI pueda mostrar el presupuesto vigente. Sin embargo, cambiar una variable de entorno normalmente requiere reiniciar el proceso; no debe presentarse como cambio dinámico inmediato.

### ¿ITAD y gg.deals tienen límite por hora o por día?

No debemos codificar una cifra fija en el plan sin verificarla contra la documentación y las condiciones de la cuenta/API vigentes. Además, un proveedor puede aplicar límites por API key, plan, endpoint, IP o ventanas deslizantes, no necesariamente por una cuota diaria simple.

Por eso el panel debe mostrar dos columnas separadas:

| Dato | Fuente | Significado |
|---|---|---|
| Presupuesto local | configuración de DealExt | Lo que la aplicación se autoimpone |
| Consumo observado | `job_runs`/métricas | Lo que realmente ha usado la aplicación |
| Límite anunciado | configuración/documentación verificada | Referencia operativa, no necesariamente detectable automáticamente |
| Rate limit recibido | respuesta real del proveedor | Evidencia de throttling, `Retry-After` o HTTP 429 |

Si se necesita cambiar el presupuesto sin reiniciar, la opción más segura es almacenar **overrides no secretos** en una tabla administrativa con auditoría, límite mínimo/máximo y botón de restaurar configuración. Las variables de entorno siguen siendo el valor base y el fallback. No se deben guardar API keys ni secretos en esa tabla.

La precedence propuesta es:

```text
override administrativo validado > variable de entorno > valor predeterminado conservador
```

El override debe tener `updatedBy`, `updatedAt`, motivo y fecha opcional de expiración. La UI debe mostrar claramente si el valor proviene de `override`, `environment` o `default`.

Cada cliente debe reportar eventos normalizados cuando observe una respuesta limitada o un retry por `Retry-After`:

```text
provider: steam | itad | ggdeals | epic | microsoft
reason: http_429 | retry_after | provider_throttle | client_budget
attempt: integer
retryAfterSeconds: integer?
observedAt: timestamp
```

El panel debe distinguir:

1. **Límite HTTP de DealExt**: protege llamadas entrantes a la API.
2. **Rate limit del proveedor**: respuesta, cabecera o señal del cliente externo.
3. **Pacing interno**: espera deliberada entre llamadas, que no equivale a un bloqueo.
4. **Error de red/servidor**: no debe contarse como rate limit.

No exponer cabeceras completas, tokens, URLs con credenciales ni excepciones crudas. El backend conserva solo códigos y datos operativos necesarios.

## Persistencia y coordinación

### Evolución recomendada

1. Mantener `job_runs` como tabla de ejecuciones.
2. Añadir columnas o una tabla hija para métricas por fase/proveedor; preferir tabla hija si se necesita consultar y agregar sin parsear JSONB.
3. Mantener `details` para datos específicos, pero con `detailsVersion` y un contrato documentado.
4. Añadir un claim atómico/lease en PostgreSQL:
   - clave estable del job;
   - owner/instance id no sensible;
   - `leaseUntil`;
   - timestamps de heartbeat si el job es largo.
5. Definir qué ocurre si falla el registro: el comportamiento actual fail-open debe revisarse, porque puede duplicar ejecuciones y falsear el centro de control.
6. Añadir retención y un proceso de purga seguro.

`RanWithinAsync` no debe considerarse un lock: consulta, pero no reclama atómicamente la ejecución.

## API y UI propuestas

### API administrativa

Rutas sugeridas, sujetas a la convención final del proyecto:

- `GET /api/admin/jobs` — catálogo, estado actual y última ejecución por job.
- `GET /api/admin/jobs/runs` — historial paginado con filtros por job, proveedor, estado, trigger y rango de fechas.
- `GET /api/admin/jobs/runs/{id}` — detalle de ejecución y fases.
- `POST /api/admin/jobs/{job}/runs` — solicitar ejecución manual, devolviendo `jobRunId`.
- `POST /api/admin/jobs/runs/{id}/cancel` — solicitar cancelación cooperativa, si se implementa en la misma fase.
- `POST /api/admin/jobs/runs/{id}/retry` — crear una nueva ejecución controlada, nunca mutar la anterior.

Todos los endpoints deben usar autorización administrativa y paginación/límites estrictos. El endpoint de detalle debe sanitizar errores antes de serializar.

### Sección web

- Nueva ruta protegida `/jobs` o `/admin/jobs`.
- Resumen superior: jobs activos, última ejecución, fallos recientes, rate limits recientes.
- Tabla de ejecuciones: job, trigger, inicio, duración, estado, procesados, errores, rate limits.
- Filtros y paginación.
- Detalle expandible por fase/proveedor.
- Acciones separadas y confirmadas para ejecutar, reintentar o cancelar.
- Polling moderado mientras una ejecución esté `running`; no usar streaming en V1.
- No mostrar stack traces, tokens, connection strings ni payloads completos de proveedores.

## Fases de implementación propuestas

### Fase 0 — Decisiones y contrato

- Confirmar audiencia, acciones permitidas, retención y política de errores.
- Definir catálogo de jobs y nombres estables.
- Definir estados y contrato de métricas.
- Definir qué proveedores reportan rate limits en V1.
- Elegir columnas frente a tabla hija para métricas por fase.

**Salida:** contrato aprobado, sin cambios de código funcional todavía.

### Fase 1 — Observabilidad fiable

- Normalizar `JobRunLog`.
- Registrar `FxRateRefreshJob`.
- Integrar wishlist y biblioteca con el contrato común.
- Añadir métricas de procesamiento y proveedor.
- Persistir rate limits reales y retries sanitizados.
- Añadir claim/lease para evitar duplicados.
- Añadir migración y checks de integridad.

**Salida:** historial confiable aun sin UI completa.

### Fase 2 — API administrativa

- DTOs y endpoints de lectura.
- Paginación, filtros y detalle.
- Autorización `AdminWithId`.
- Sanitización y límites de respuesta.
- Endpoint asíncrono de ejecución manual si se confirma.

**Salida:** API consumible por la sección web.

### Fase 3 — Centro de control web

- Contratos TypeScript y BFF.
- Página `/jobs` o `/admin/jobs`.
- Resumen, tabla, filtros y detalle.
- Polling de ejecuciones activas.
- Navegación administrativa.

**Salida:** visibilidad operacional usable.

### Fase 4 — Acciones operativas opcionales

- Reintento como nueva ejecución.
- Cancelación cooperativa.
- Purga/retención desde tarea controlada, no desde una acción destructiva libre.
- Auditoría de quién solicitó una ejecución.

**Salida:** control operativo, no solo observabilidad.

## Hangfire: criterio de decisión

### No introducirlo en V1

El sistema tiene pocos jobs, un modelo de hosting ya funcional y persistencia propia. Introducir Hangfire ahora duplicaría conceptos (`job_runs` frente a historial Hangfire) y no resolvería la semántica de fases, proveedores, rate limits ni la idempotencia de cada sync.

### Reevaluarlo si aparece cualquiera de estas señales

- múltiples réplicas de API ejecutando schedulers;
- necesidad real de colas persistentes y trabajos diferidos por usuario;
- jobs largos que deban sobrevivir al reinicio con reanudación;
- varios tipos de prioridad/concurrencia y reintentos administrables;
- necesidad de dashboard operativo maduro fuera de la aplicación;
- crecimiento suficiente para justificar un worker separado.

Si se adopta, debe decidirse si Hangfire reemplaza el scheduler y el historial o solo ejecuta; mantener dos fuentes de verdad sería un error.

## Fuera de alcance inicial

- Reemplazar todos los logs de aplicación.
- Exponer dashboards de infraestructura o Docker.
- Registrar payloads completos de proveedores.
- Garantizar métricas históricas retroactivas que el sistema nunca persistió.
- Crear un job separado por cada tienda sin necesidad operativa demostrada.
- Introducir Hangfire antes de resolver idempotencia, locks y contrato de métricas.

## Criterios de aceptación del plan

- Se puede saber cuál fue la última ejecución de cada job y su estado.
- Se distinguen procesados, correctos, fallidos, omitidos, retries y rate limits reales.
- FX y los jobs de precios no desaparecen del historial.
- Dos instancias no pueden reclamar simultáneamente el mismo job programado.
- Una ejecución manual no bloquea una petición HTTP durante minutos.
- Los errores visibles están sanitizados.
- Existe retención definida y las consultas del panel son paginadas.
- La decisión sobre Hangfire se basa en señales operativas medibles, no en preferencia tecnológica.
