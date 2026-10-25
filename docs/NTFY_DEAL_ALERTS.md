# Alertas personales: filtro de operador y seguridad de envío

El notificador dejó de ser global: alerta **solo los favoritos + la wishlist de un operador** (la cuenta 1 por
defecto; otra cuenta solo con override explícito), y revisa ese interés otra vez justo antes de enviar. El `ntfy`
sigue siendo un tema único, sin modelo multiusuario.
Contexto previo del canal: [`NTFY_NOTIFICATIONS.md`](NTFY_NOTIFICATIONS.md). Plantilla de variables:
[`NTFY_SETTINGS.md`](NTFY_SETTINGS.md).

## Camino rápido

1. **No hay que configurar nada para uso personal:** el operador por defecto es la cuenta **1**, la del dueño
   del despliegue. `Ntfy__UserId` solo se pone si el operador debe ser otra cuenta (> 0).
2. Poner `Ntfy__Enabled=true` y reiniciar la API. Un id **explícito** no positivo (0 o negativo) es un error de
   configuración y la API **no arranca** con el notificador habilitado.
3. Verificar sin enviar nada: `dotnet run --project checks/Ntfy.Checks`.

No hay migración nueva ni cambio de esquema: la tabla `deal_alert_events` y su `dedup_key` se conservan.

### Prerrequisito de despliegue

`docker-compose.yml` reenvía el operador al contenedor como `Ntfy__UserId=${Ntfy__UserId:-1}`: si la
configuración privada del despliegue no define el id, el contenedor recibe **1**, que es la cuenta del
despliegue de un solo usuario. Ese valor no se adivina ni se infiere del admin ni del llamador: es el default
fijo del código y de la plantilla, y queda en la cuenta existente número 1.

**Si esa cuenta no existe o está inactiva, el id positivo no basta:** `DealAlertRecipientScope` no encuentra la
cuenta y el ciclo entero falla cerrado (el productor no encola y el despachador no reclama). Es un fallo
silencioso deliberado —no un crash— así que la comprobación útil es la consulta de la cuenta, no el log de
arranque. Además ese usuario debe tener acceso al tema configurado: con tema protegido, el `Ntfy__AuthToken`
correspondiente se pone en la configuración privada del despliegue, jamás en el repositorio. Este documento no
contiene ids reales ni secretos.

## Qué entra al alcance

| Fuente | Fila leída | Identidad que aporta |
|---|---|---|
| Favoritos | `user_game_favorites` del operador | `game_id` canónico |
| Wishlist | `user_library` con `state='wished'` del operador | `game_id` si existe; si no, `store='steam'` + `store_game_id` (appid) |
| Mapeo Steam | `steam_games` con `region='mx'` | appid → `steam_game_id` exacto, y su `game_id` si está resuelto |

El alcance es una **unión**: un juego que está en favoritos o en la wishlist entra. Un `game_id` canónico se
expande a los `steam_game_id` que ese juego posee, porque una oferta puede colgar de `steam_game_id` sin llevar
`game_id`. Nunca se resuelve por título.

## Puertas de seguridad (en orden)

| Puerta | Dónde | Si no se cumple |
|---|---|---|
| Id positivo (default **1**, override opcional) | validación de opciones (arranque) | con un id explícito no positivo y `Enabled=true`, la API no arranca |
| Cuenta del operador existente y activa | `DealAlertRecipientScope.LoadAsync` | productor y despachador fallan cerrado: no leen, no encolan, no reclaman |
| Id no positivo (override explícito 0) | `DealAlertRecipientScope.LoadAsync` (antes de cualquier consulta) | igual que arriba, sin consultar nada |
| Interés vigente al despachar | `DealAlertDispatcher`: relee el alcance **por fila reclamada**, después de revalidar la oferta y antes de `sender.SendAsync` | la fila pasa a `stale`; no se envía, no gasta intento |
| Misma cuenta al despachar | la relectura debe devolver el mismo `UserId` que reclamó la fila | si el operador cambió, la fila pasa a `stale` |
| Cuenta desactivada al despachar | la relectura devuelve null | la fila pasa a `stale` |
| Identidad conocida | `DealAlertDispatcher`, por fila | una fila vieja sin `game_id` ni `steam_game_id` pasa a `stale` |
| Oferta viva y mismo dedup | `DealAlertCandidateReader.FindCurrentAsync` | la fila pasa a `stale` |

El filtro de interés del productor se aplica **en SQL** (`game_id`/`steam_game_id` dentro del alcance) antes de
materializar ofertas, y otra vez en memoria antes de encolar: un lector que devuelva de más no puede encolar la
oferta de otra persona.

El alcance es una **dependencia obligatoria** del productor y del despachador: no hay constructor sin él, así que no
existe un camino que produzca o envíe sin filtro. Tampoco se cachea entre ciclos ni durante un ciclo: la primera
lectura solo sirve para decidir si se reclama, y cada envío exige su propia relectura.

## Precio directo de Steam

`steam_games` con `observed_at` fresco y precio **nativo en MXN** entra como candidato con `source='steam'`:

- compite por **precio** y por **nota**;
- **nunca** por mínimo histórico: `HistoryLowAllMinor` se deja en `null` y `DealAlertPolicy` rechaza
  `source='steam'` de esa regla. El mínimo local lo crean nuestras propias observaciones y no es evidencia;
- `offer_key` es el appid; el `click_url` es la ficha pública `store.steampowered.com/app/<appid>/`;
- la nota usa el precio de lista de Steam como base y el mínimo en MXN reportado por los proveedores.

En la revalidación previa al envío, `FindCurrentAsync` resuelve `source='steam'` por `OfferKey` (appid) y
`steam_game_id`, exige moneda MXN y **`game_id` exacto**: si la identidad canónica cambió, la fila es `stale`.

## Backlog y cambio de operador

- Sin operador válido **no se reclama nada**: ni con un id explícito no positivo, ni cuando la cuenta
  configurada (1 por defecto) no existe o está inactiva. Los pendientes se conservan para cuando la cuenta
  exista o vuelva a estar activa, en vez de quemarse como `stale`. La puerta depende del id y de la cuenta, no de
  `Enabled`: un ciclo manual con el notificador deshabilitado tampoco produce ni envía.
- Un pendiente que ya no está en el alcance (desfavorito, fila de wishlist eliminada, id cambiado) se marca
  `stale`: no se envía y no consume intentos.
- Las filas `sent` son inmutables: no se reclaman, no se reescriben y no se reenvían.
- **Limitación documentada:** el dedup es por oferta + precio. Si se cambia de operador, una alerta que ya se
  envió **al mismo precio no se repite** para el nuevo operador. Tampoco se replica el historial: la tabla no
  guarda usuario. Es el precio de no subir `TriggerVersion`, que reenviaría todo el catálogo vigente.
- La tabla no tiene columna de usuario; no se agregó ninguna. El alcance se evalúa contra la configuración viva.

## Configuración

```dotenv
Ntfy__Enabled=false        # master switch; en false el job sale al arrancar
Ntfy__UserId=1             # operador; default 1 = cuenta de este despliegue de un solo usuario (override opcional)
Ntfy__BaseUrl=https://ntfy.amitzi.xyz/
Ntfy__Topic=Deals
Ntfy__AuthToken=SET_NTFY_AUTH_TOKEN
```

El resto de las opciones conserva sus defaults (ver `NTFY_NOTIFICATIONS.md §7`). El id **nunca** se infiere del
admin ni del llamador de un endpoint: es el default fijo 1 o el override explícito. No hay valores reales en
`.env.example`. `docker-compose.yml` es el único punto que reenvía el id al contenedor, con default `1`; la
regresión de ese reenvío vive en `checks/ntfy-compose.test.mjs` y se corre sin Docker y sin leer `.env`.

## Verificación

```bash
dotnet run --project checks/Ntfy.Checks
node --test checks/ntfy-compose.test.mjs   # reenvío del operador en docker-compose.yml, sin Docker
```

El segundo comando lee el compose como texto plano (no ejecuta `docker compose config`, así que no resuelve ni
imprime `.env`).

Cubre, con dobles en memoria y sin envíos reales: el default 1 como operador real (la cuenta 1 se lee de la
tabla de cuentas), la falla cerrada cuando esa cuenta no existe o está inactiva, el rechazo de arranque de un
override explícito no positivo, el 0 explícito que falla cerrado antes de cualquier consulta, la unión
favoritos+wishlist dentro del alcance, un pendiente que salió del alcance retirado como `stale`, un backlog sin
identidad retirado, la entrega del interés vigente, que cambiar de operador no reenvíe filas `sent` y que el
grafo real de DI (sin validación de arranque) no reclame ni encole con un operador explícito 0, mientras que sin
configuración sí resuelve la cuenta 1 y ejecuta el ciclo. También cubre las carreras del ciclo: interés
eliminado, cuenta desactivada y operador cambiado **después** de reclamar y **antes** de enviar no se entregan;
la relectura se observa en el número de lecturas del doble. Los checks previos de política, sender y dispatcher
se conservan y ahora construyen el alcance explícito.

## Límites conocidos

| Límite | Motivo |
|---|---|
| Sin prueba de integración EF | el repo no tiene proveedor en memoria; la consulta de alcance y el mapeo de Steam se validan por revisión y por los checks de contrato, no ejecutando SQL |
| Sin paridad con el catálogo compartido | el proyector de candidatos compartido (T2) todavía no existe; este lector es el contrato vigente y el productor aplica el mismo alcance en memoria |
| Cambiar de operador no reenvía | el dedup no distingue usuario y subir `TriggerVersion` reenviaría todo |
| Cuenta bloqueada por login | la puerta usa `active`; `locked_until` es un bloqueo de autenticación y no se interpreta como baja del operador |
| Default 1 silencioso | si en un despliegue real la cuenta 1 no existe o está inactiva, el ciclo no envía y no falla el arranque: el síntoma es "no llega nada", no un error |
| Ventana residual entre relectura y POST | la relectura ocurre inmediatamente antes del envío; lo que cambie entre esa lectura y el POST no se puede observar sin transacción sobre ntfy, que no existe |
| Costo de la relectura | una lectura de alcance por fila reclamada, acotada por `Ntfy__DispatchBatchSize` (20 por defecto); son consultas de solo lectura sobre tablas de identidad |
