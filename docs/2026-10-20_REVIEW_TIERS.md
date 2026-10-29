# Tier list anual atada a la partida reseñada

> Estado: **implementado, migración sin aplicar** · Tarea `odd/tasks/discover-and-review-instance-tiers.md` (T1, specs S3-S6) · No cambia precios ni ahorros.

La tier list de `/library/tiers` deja de clasificar **juegos** y pasa a clasificar **partidas reseñadas**. Cada reseña —incluida una segunda partida del mismo juego en la misma plataforma— es una tarjeta con rango independiente, muestra su plataforma y, si la reseña tiene GOTY, un marco y un texto distintos.

## Qué cambia

| Pieza | Dónde |
|---|---|
| Puesto con identidad de reseña | `Deals.Models/Entities/UserGameTier.cs`, `AppDbContext.UserGameTiers` |
| Años y puestos por partida | `Deals.BusinessLogic/Interfaces/IGameTierService.cs`, `Services/GameTierService.cs` |
| Endpoints | `Deals.API/Controllers/GameTiersController.cs` (`GET /api/game-tiers/years`, `GET /api/game-tiers?year=`, `PUT /api/game-tiers?year=`) |
| Ancla de fusión | `Deals.BusinessLogic/Services/GameMergeService.cs` |
| Esquema y migración | `SQL/schema.sql`, `SQL/migrations/2026-10-20_user_game_tiers_per_review.sql` |
| Invariantes | `SQL/checks/game_tiers_reviews.sql` |
| Contrato y helpers del cliente | `Deals.Web/lib/contracts/game-tiers.ts`, `Deals.Web/app/library/tiers/_lib/tier-list.ts` |
| BFF | `Deals.Web/app/api/bff/game-tiers/route.ts`, `Deals.Web/app/api/bff/game-tiers/years/route.ts` |
| UI | `Deals.Web/app/library/tiers/tier-list-client.tsx` |
| Checks deterministas | `checks/GameTiers.Checks` |

### Modelo

- PK `(user_id, year, game_review_id)`. `game_id` se conserva **solo** como ancla de fusión y tiene que seguir coincidiendo con el `game_id` de su reseña.
- FK `game_review_id → game_reviews(game_review_id) ON DELETE CASCADE`: borrar una reseña borra su tarjeta. FK `game_id → games ON DELETE CASCADE`: borrar el juego borra la tarjeta aunque la reseña ya no exista.
- `year` es la clave de la lista (`GetForYear` filtra por el año de la reseña), pero es una columna desnormalizada. El servicio rechaza guardar en un año que no sea el de la reseña.
- Permisos sin cambios: la página sigue siendo admin-only (`requireAdminSession`) y las rutas BFF conservan su comprobación de rol; el backend sigue con `[Authorize(Policy = "UserWithId")]`, y la pertenencia de la reseña se comprueba por usuario (la reseña de otra persona es un 404, no un 403).

### Contrato

`GET /api/game-tiers/years` → `[2026, 2025]` (años distintos derivados por reseña de `finished_month ?? started_month`, más reciente primero; una reseña sin fechas no aporta año).

`GET /api/game-tiers?year=2026` → una tarjeta por reseña fechada:

```json
[{ "reviewId": 12, "gameId": 7, "title": "Portal", "imageUrl": null,
   "platform": "ps5", "isGoty": true, "tier": "S", "position": 0 }]
```

`PUT /api/game-tiers?year=2026` con `{ "reviewId": 12, "tier": "A", "position": 3 }` → `204`, o `404` si la reseña no es del usuario o no está fechada en ese año. El cliente envía `reviewId`, no `gameId`: un `gameId` no distingue dos partidas del mismo juego.

### Cliente

- Pide **primero** los años (`/api/bff/game-tiers/years`) y abre en el más reciente; sin reseñas fechadas muestra un estado vacío en lugar de inventar el año actual.
- Error con `Reintentar`; las recargas de años y de tarjetas y los guardados usan un guardia de versión (`createVersionGate`), así que una respuesta o un guardado de un año anterior que llega tarde no pisa la lista vigente.
- Arrastre, selector y orden usan `reviewId`; el orden dentro de un rango es posición y, en empate, `reviewId` (nunca el título), para que dos partidas del mismo juego no bailen entre recargas.
- Plataforma con `storeLabel` (mismo vocabulario que la biblioteca y las reseñas). GOTY pinta `border-2 border-[color:var(--color-success)]` y el badge semántico `tabler-badge-success` con el texto `GOTY`, más "partida n" en el `aria-label` para distinguir dos tarjetas idénticas.

## Migración manual (no aplicar a la base configurada desde este cambio)

`SQL/migrations/2026-10-20_user_game_tiers_per_review.sql` es de ejecución **manual**, una sola vez, y no se aplicó aquí.

Backfill no destructivo: cada puesto viejo se **copia con su rango y posición a todas las reseñas fechadas** del mismo usuario, juego y año. El diseño descartado —asignar el puesto a la reseña más nueva— borraba el puesto de las otras partidas. Solo se eliminan los **huérfanos**: puestos sin ninguna reseña fechada que los respalde (reseña eliminada o con el año editado antes de migrar). Esa pérdida es intencional y queda documentada en el encabezado de la migración, con la consulta de solo lectura que lista los huérfanos **antes** de aplicar.

Orden de despliegue obligatorio:

1. **Antes de aplicar**, correr las consultas de preflight del encabezado de la migración y guardar el resultado (en especial la lista de huérfanos, que se van a borrar).
2. Respaldar la tabla: `CREATE TABLE user_game_tiers_backup_20261020 AS SELECT * FROM public.user_game_tiers;` (o un `pg_dump` de la base).
3. Aplicar la migración. Deja la tabla con PK nueva, FK en CASCADE e índices de ambos FK.
4. Correr `SQL/checks/game_tiers_reviews.sql`: cada consulta debe devolver **0 filas**.
5. **Después** desplegar API y frontend. El binario nuevo no arranca funcionalmente contra el esquema viejo (insertaría sin `game_review_id`), y el binario viejo tampoco contra el esquema nuevo (su PK no coincide). La ventana es de despliegue, no de convivencia: migrar y desplegar seguido.

Rollback: **no hay downgrade sin pérdida.** Volver al esquema viejo obliga a colapsar `(user_id, year, game_review_id)` a `(user_id, year, game_id)`, y la copia no guarda cuál de las tarjetas era el puesto original: el rollback tendría que elegir una y borrar las demás. El rollback real es restaurar el respaldo del paso 2, o aceptar esa pérdida. El único rollback soportado de la aplicación es el que ya existía (fusiones de juegos), no este.

### Deriva de año (deliberada)

Si una reseña se edita a otro año después de guardar su tarjeta, el puesto se queda en el año viejo: `GetForYear` filtra por el año de la reseña, así que la tarjeta queda invisible en el año nuevo (aparece "Sin clasificar") y el puesto viejo no se reutiliza. Mover datos de vuelta requeriría adivinar la intención del usuario, así que no se hace. Por eso `game_tiers_reviews.sql` **no** impone un invariante de año ni se inventa un CHECK/trigger que la tabla no puede sostener sin prohibir la edición legítima de fechas; la consulta de diagnóstico queda comentada en ese archivo.

## Verificación

```bash
node --test Deals.Web/lib/contracts/game-tiers.test.ts
node --test Deals.Web/app/library/tiers/_lib/tier-list.test.ts
dotnet run --project checks/GameTiers.Checks          # S3-S6 deterministas, sin PostgreSQL
dotnet build Deals.sln
cd Deals.Web && pnpm exec tsc --noEmit
```

`checks/GameTiers.Checks` usa el proveedor in-memory y un repositorio mínimo: cubre años por reseña, dos partidas del mismo juego con rangos independientes, rechazo por dueño y por año sin escribir nada, y la concordancia entre modelo EF, `SQL/schema.sql`, la migración, `GameMergeService` y el archivo de invariantes. La migración en sí **no** se probó contra PostgreSQL: no hay base local disponible en este cambio, así que el SQL queda pendiente de una corrida controlada antes de producción.
