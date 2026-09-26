# Categorías de wishlist

> **Estado:** plan aprobado, no implementado. Este documento propone la evolución de la wishlist de Steam para organizar juegos mediante categorías opcionales sin romper la sincronización ni el preview de paquetes.
>
> **Referencias:** [`PLAN_WISHLIST.md`](PLAN_WISHLIST.md), [`PLAN_WISHLIST_PAQUETE.md`](PLAN_WISHLIST_PAQUETE.md), `Deals.Models/Entities/UserLibrary.cs`, `Deals.BusinessLogic/Services/WishlistSyncService.cs`, `Deals.API/Controllers/WishlistController.cs`, contratos/UI bajo `Deals.Web/app/wishlist/` y `SQL/schema.sql`.

## Decisión ejecutiva

Añadir categorías propias de la wishlist mediante una relación muchos-a-muchos opcional:

- Una entrada `user_library` con `state = 'wished'` puede pertenecer a cero, una o varias categorías.
- Una categoría pertenece a un usuario y solo puede aplicarse a entradas de wishlist de ese mismo usuario.
- La prioridad se seguirá leyendo de `user_library.priority`, reutilizando el campo existente. **La sincronización no debe sobrescribir la prioridad local en filas existentes.** En filas nuevas, la prioridad inicial será la recibida de Steam para conservar el orden útil de la primera importación; después, esa prioridad queda bajo control local.
- La UI ofrecerá `Todas`, `Sin categoría` y cada categoría individual. La selección múltiple seguirá identificando juegos por AppID y reutilizará el preview de paquetes existente, sin duplicar la lógica de precios.
- No se añade registro público ni se cambia el modelo single-admin actual. La autorización debe comprobar pertenencia al usuario autenticado y pertenencia del juego a su wishlist.

## Alcance y no alcance

### En alcance

| Área | Decisión |
|---|---|
| Persistencia | Tablas para categorías y asignaciones many-to-many, con cascadas e índices. |
| Sync | Preservar `priority` local en updates; no tocar categorías durante la sincronización de Steam. |
| API | Lectura de categorías junto con wishlist y operaciones de creación, renombrado, eliminación y asignación en lote. |
| UI | Filtro por categoría, filtro explícito de juegos sin categoría, creación/edición rápida y asignación múltiple. |
| Paquetes | Seleccionar juegos de un grupo y enviar sus AppIDs al mismo `package-preview`; precios y autorización permanecen centralizados. |
| Verificación | Invariantes SQL, builds, pruebas de API/UI y smoke manual con más de una categoría. |

### Fuera de alcance

- Categorías globales o compartidas entre usuarios.
- Sincronizar categorías con Steam: Steam aporta wishlist, prioridad inicial y fecha, pero no categorías de DealExt.
- Cambiar el significado de `user_library.priority` o crear una segunda prioridad.
- Guardar paquetes nombrados, presupuestos, cupones o ahorro entre escenarios.
- Duplicar consultas de precios, subtotalización o el endpoint de preview.
- Registro público, multiusuario de producto o administración de usuarios fuera de las políticas existentes.
- Secretos, credenciales, tokens o valores de configuración sensible.

## Evidencia del estado actual

- `user_library` ya tiene `priority INT NULL`, una unicidad por `(user_id, store, store_game_id, state)` y estados que distinguen `wished` de `owned`.
- `UserLibrary.Priority` está mapeado directamente a esa columna en `Deals.Models/Entities/UserLibrary.cs`.
- `WishlistSyncService.UpsertLibraryAsync` recibe `item.Priority`, lo usa al insertar y actualmente reasigna `entry.Priority` al actualizar. **Ese assignment debe eliminarse en la implementación futura.**
- La wishlist expone `priority` en sus contratos y la UI actual ya puede ordenarla.
- `WishlistController` usa `AdminWithId`, carga la wishlist del usuario autenticado y ya expone `POST /api/wishlist/package-preview`.
- `PLAN_WISHLIST_PAQUETE.md` establece que el preview recibe AppIDs, vuelve a leer precios en servidor, valida pertenencia y limita la petición a 200 AppIDs. Las categorías deben aprovechar exactamente ese flujo.

## Modelo de datos propuesto

Los nombres son propositivos; la migración futura debe conservar las mismas invariantes aunque ajuste la nomenclatura.

```sql
CREATE TABLE wishlist_categories (
    wishlist_category_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    name VARCHAR(80) NOT NULL,
    normalized_name VARCHAR(80) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_wishlist_categories_user_name UNIQUE (user_id, normalized_name),
    CONSTRAINT uq_wishlist_categories_user_id UNIQUE (user_id, wishlist_category_id),
    CONSTRAINT ck_wishlist_categories_name_nonblank CHECK (length(btrim(name)) > 0)
);

CREATE TABLE wishlist_category_items (
    wishlist_category_id BIGINT NOT NULL,
    user_id INT NOT NULL,
    user_library_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    PRIMARY KEY (wishlist_category_id, user_library_id),
    CONSTRAINT fk_wishlist_category_items_category
        FOREIGN KEY (user_id, wishlist_category_id)
        REFERENCES wishlist_categories(user_id, wishlist_category_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_wishlist_category_items_library_user
        FOREIGN KEY (user_id, user_library_id)
        REFERENCES user_library(user_id, user_library_id)
        ON DELETE CASCADE
);

CREATE UNIQUE INDEX uq_user_library_user_library_id
    ON user_library(user_id, user_library_id);

CREATE INDEX idx_wishlist_categories_user
    ON wishlist_categories(user_id, lower(name));

CREATE INDEX idx_wishlist_category_items_user_category
    ON wishlist_category_items(user_id, wishlist_category_id, user_library_id);

CREATE INDEX idx_wishlist_category_items_user_library
    ON wishlist_category_items(user_id, user_library_id, wishlist_category_id);
```

### Notas del DDL

1. `normalized_name` debe generarse en la capa de aplicación con trim y comparación insensible a mayúsculas/minúsculas. No se deben almacenar dos categorías del mismo usuario que solo difieran por capitalización o espacios exteriores.
2. La FK compuesta con `user_id` evita que una asignación mezcle una categoría de un usuario con una fila de biblioteca de otro, incluso si un endpoint tuviera un defecto de autorización.
3. La FK hacia `user_library` no por sí sola restringe `state = 'wished'` ni `store = 'steam'`; el servicio debe validarlo en todas las lecturas y escrituras. Si se desea una garantía adicional, puede añadirse una clave/trigger específica en una fase posterior, pero no es necesaria para V1.
4. Al eliminar un usuario, una categoría o una fila de wishlist, las asignaciones se eliminan en cascada. La eliminación de una categoría **no elimina juegos**.
5. La unicidad de `(wishlist_category_id, user_library_id)` hace idempotente la asignación en lote.
6. Las categorías no deben cambiar `user_library.imported_at`, `added_at` ni `priority`.

## Regla exacta de sincronización

La sincronización de Steam debe aplicar estas reglas en `WishlistSyncService.UpsertLibraryAsync`:

| Caso | `priority` |
|---|---|
| Fila nueva | Inicializar con `item.Priority` recibido de Steam; `NULL` es válido si Steam no lo entrega. |
| Fila existente | No asignar `entry.Priority`; conservar exactamente el valor local, incluido `NULL`. |
| Fila que deja de aparecer | El comportamiento actual de prune para filas `wished` se mantiene; sus asignaciones se eliminan por cascada. |
| Fila `owned` u otro estado | Nunca modificarla ni asignarle categorías de wishlist. |
| Re-sincronización | No mover ni borrar categorías. Solo la desaparición de la fila wishlist elimina sus asignaciones indirectamente. |

La prioridad deja de ser una copia continuamente autoritativa de Steam: Steam aporta el valor inicial, mientras que el usuario puede organizar localmente su wishlist. Esta regla debe quedar cubierta por una prueba de regresión que ejecute dos syncs con prioridades distintas y confirme que la segunda no reemplaza el valor existente.

## API propuesta

Todos los endpoints deben resolver el usuario desde la identidad autenticada. No deben aceptar `userId` como autoridad del body o de la URL. La pertenencia se verifica con una consulta que incluya `user_id`, `store = 'steam'` y `state = 'wished'`.

### Lectura

| Método y ruta | Propósito | Autorización |
|---|---|---|
| `GET /api/wishlist` | Mantener la respuesta actual y añadir categorías por item, o un bloque `categories` normalizado. | Usuario autenticado; datos propios. |
| `GET /api/wishlist/categories` | Listar categorías del usuario con conteo de juegos asignados. | Usuario autenticado; datos propios. |

La opción preferida es extender `WishlistResponse` con una lista de categorías por AppID y una lista global de categorías, manteniendo la fuente de precios y el shape de los campos existentes. No duplicar una consulta de wishlist distinta para cada filtro.

### Escritura individual

| Método y ruta | Body mínimo | Resultado |
|---|---|---|
| `POST /api/wishlist/categories` | `{ "name": "RPG" }` | Crea una categoría propia; `409` si el nombre normalizado ya existe. |
| `PATCH /api/wishlist/categories/{categoryId}` | `{ "name": "RPG cooperativo" }` | Renombra sin cambiar asignaciones; `404` si no pertenece al usuario. |
| `DELETE /api/wishlist/categories/{categoryId}` | — | Elimina categoría y asignaciones; no elimina juegos. |
| `PUT /api/wishlist/items/{appId}/categories` | `{ "categoryIds": [1, 2] }` | Reemplaza las categorías del juego tras validar pertenencia. |

### Operaciones en lote

| Método y ruta | Body mínimo | Reglas |
|---|---|---|
| `POST /api/wishlist/categories/{categoryId}/items` | `{ "appIds": [1057800, 292030] }` | Asigna todos los AppIDs propios de la wishlist a la categoría; operación idempotente. |
| `DELETE /api/wishlist/categories/{categoryId}/items` | `{ "appIds": [1057800, 292030] }` | Quita asignaciones; no elimina wishlist ni categorías. |
| `PUT /api/wishlist/categories/{categoryId}/items` | `{ "appIds": [...] }` | Reemplaza el conjunto de esa categoría; debe exigir confirmación UI si vacía muchas asignaciones. |

Límites recomendados para V1: máximo 200 AppIDs por operación de lote, deduplicación previa y respuesta con `requested`, `matched`, `changed` y `ignored`. Los AppIDs inexistentes o ajenos no deben revelar si existen en otra cuenta. La respuesta puede reportarlos como no pertenecientes sin devolver datos adicionales.

La política actual del controlador es `AdminWithId` porque la aplicación es single-admin. Si se habilita multiusuario, estas rutas deben pasar a la política de usuario normal y conservar la misma comprobación por `user_id`.

## UX de filtros y edición rápida

### Filtros

El control de categorías debe ser explícito y no confundirse con el buscador textual:

1. **Todas**: muestra todos los juegos de la wishlist.
2. **Sin categoría**: muestra únicamente juegos que no tienen ninguna asignación.
3. **Una categoría**: muestra juegos asignados a esa categoría.
4. **Varias categorías**: permitir selección múltiple en una fase posterior de la misma UI. La semántica recomendada es **OR** (juegos de cualquiera de las categorías seleccionadas), con el texto visible `Cualquiera de`; si se necesita intersección, ofrecerla como modo explícito `Todas las seleccionadas`, nunca implícitamente.

El filtro debe combinarse con el texto de búsqueda, prioridad y selección múltiple. Al cambiar de filtro, la selección por AppID se conserva, igual que en el preview actual; `Seleccionar visibles` solo opera sobre las filas visibles.

### Edición rápida

- Cada fila muestra sus categorías como badges y una acción `Editar categorías`.
- El editor permite marcar/desmarcar categorías y crear una categoría nueva sin abandonar la wishlist.
- La selección de varias filas añade una acción `Asignar a categoría` y otra `Quitar de categoría`.
- La edición vacía es válida: significa que el juego queda en `Sin categoría`.
- Los errores de autorización, conflicto de nombre y sesión expirada deben usar los errores tipados existentes del BFF; no mostrar respuestas crudas.
- La UI debe anunciar cambios con `aria-live`, mantener foco y ofrecer confirmación no bloqueante después de operaciones en lote.

## Integración con selección y preview de paquetes

La integración debe reutilizar el flujo existente de `PLAN_WISHLIST_PAQUETE.md`:

- La selección continúa siendo `Set<number>` de AppIDs; las categorías solo cambian qué filas se muestran.
- `Seleccionar visibles` permite seleccionar todos los juegos del grupo/categoría actualmente filtrado.
- La barra de paquete sigue llamando a `POST /api/bff/wishlist/package-preview` con los AppIDs seleccionados.
- El endpoint existente vuelve a validar pertenencia y relee `BestOfficialMinor`/`BestKeyshopMinor`; no se crea un preview específico por categoría.
- Se conserva el límite de 200 AppIDs, la deduplicación, los subtotales independientes y la semántica `null` para un escenario sin cotizaciones.
- La categoría no se envía como fuente de precios ni altera el cálculo. Si en el futuro se añade “paquete por categoría”, será un atajo de selección que materializa AppIDs y usa el mismo preview.

## Fases ordenadas

### Fase 0 — Contrato y protección de la sincronización

- Fijar el contrato de categorías y la semántica de `Sin categoría`.
- Cambiar `WishlistSyncService` para no actualizar `Priority` en filas existentes.
- Añadir pruebas de regresión del sync y documentar el comportamiento.

**Salida:** dos syncs con prioridades Steam diferentes preservan la prioridad local y no modifican categorías.

### Fase 1 — Persistencia

- Añadir DDL/migración para las dos tablas, FKs, unicidad, cascadas e índices.
- Añadir entidades y relaciones al contexto sin leer ni transportar secretos.
- Verificar que una asignación solo puede apuntar a la misma cuenta y a una entrada wishlist.

**Salida:** invariantes SQL sin filas inválidas y rollback documentado antes de aplicar en producción.

### Fase 2 — API y BFF

- Implementar lectura de categorías junto con wishlist.
- Implementar CRUD de categorías y reemplazo/asignación en lote.
- Aplicar autorización por identidad y pertenencia en cada operación.
- Añadir contratos y normalizadores estrictos en el BFF.

**Salida:** pruebas de pertenencia, duplicados, categoría vacía, conflicto de nombres y sesión expirada.

### Fase 3 — UI de filtros y edición

- Añadir selector `Todas / Sin categoría / categoría`.
- Añadir badges y editor rápido individual.
- Añadir acciones en lote y accesibilidad.
- Mantener ordenamiento, paginación y selección por AppID.

**Salida:** un usuario puede crear, editar, asignar, quitar y filtrar sin perder selecciones.

### Fase 4 — Paquetes y endurecimiento

- Confirmar que `Seleccionar visibles` de una categoría llena el mismo `Set<number>`.
- Verificar que el preview usa exactamente el endpoint existente y no duplica precios.
- Añadir métricas/logs acotados para fallos de lote y conciliación de selección.

**Salida:** preview correcto para una categoría, varias categorías y `Sin categoría`, incluyendo faltantes de precios.

## Riesgos y mitigaciones

| Riesgo | Mitigación |
|---|---|
| Steam vuelve a sobrescribir una prioridad local | Prueba de regresión sobre `WishlistSyncService`; revisión explícita del assignment de `entry.Priority`. |
| Categoría o juego de otra cuenta se expone | FKs compuestas y consultas siempre filtradas por identidad; pruebas negativas de autorización. |
| Una asignación apunta a `owned` | Validación de servicio `store='steam' AND state='wished'`; no confiar solo en el FK. |
| Conflictos de nombres por mayúsculas/espacios | `normalized_name`, trim, límite de longitud y `409` determinista. |
| Borrar categoría borra la wishlist | FK `ON DELETE CASCADE` solo desde asignaciones hacia categoría; nunca desde categoría hacia `user_library`. |
| Selección se pierde al filtrar | Selección estable por AppID, no por índice; reconciliación solo si la fila deja de existir. |
| Se duplica la lógica de precios | Categorías producen AppIDs; el preview existente sigue siendo la única fuente de subtotales. |
| Usuario interpreta dos escenarios como suma | Mantener subtotales oficial/keys separados y la advertencia existente de paquete alternativo. |
| Operaciones en lote demasiado grandes | Dedupe y máximo de 200 AppIDs, alineado con el preview actual. |
| El cambio de sync elimina categorías de forma inesperada | Solo el prune de una wishlist que realmente deja de contener la fila elimina asignaciones por cascada; un sync exitoso no toca categorías de filas presentes. |

## Verificación y evidencia

### Verificación técnica mínima

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
node --test app/wishlist/_lib/
```

No hay proyecto de tests .NET; por ello la cobertura debe combinar pruebas de servicio/controlador, pruebas de utilidades UI, invariantes SQL y smoke manual. No incluir secretos ni valores de `.env` en ningún reporte.

### Evidencia por afirmación

| Afirmación | Evidencia mínima | Límite conocido |
|---|---|---|
| La prioridad local sobrevive al sync | Test con fila existente, prioridad local A y payload Steam B; resultado A. Test adicional para fila nueva. | No prueba límites del proveedor Steam. |
| Solo se autorizan datos propios | Tests con categoría/AppID propios, ajenos, inexistentes y `owned`; respuestas sin filtración. | El controlador actual es single-admin; repetir con política multiusuario si se habilita. |
| DDL mantiene integridad | Aplicar en una BD aislada y ejecutar inserts válidos/ inválidos, deletes de usuario/categoría/wishlist y consultas de índices. | No sustituye revisión de migración/rollback de producción. |
| Filtros son correctos | Smoke con juegos en cero, una y varias categorías; comprobar `Todas`, `Sin categoría` y combinaciones. | La semántica OR/AND de multi-filtro debe quedar visible en UX antes de implementarla. |
| El paquete no duplica precios | Seleccionar visibles desde una categoría y comparar request/response con selección manual de los mismos AppIDs. | El preview en vivo requiere PostgreSQL y configuración de ejecución disponible. |
| La UI conserva selección | Prueba de ordenamiento, búsqueda, filtro, paginación y desaparición de una fila. | Sin E2E existente, parte del smoke será manual. |

### Criterios de aceptación

- [ ] Una wishlist sincronizada dos veces no pierde una prioridad local editada; una fila nueva puede recibir la prioridad inicial de Steam.
- [ ] Un juego puede estar en cero, una o varias categorías sin duplicar la fila de wishlist.
- [ ] `Sin categoría` muestra exactamente las filas sin asignaciones.
- [ ] Crear, renombrar, eliminar y asignar categorías valida pertenencia al usuario autenticado.
- [ ] Eliminar una categoría no elimina ningún juego ni cambia `priority`.
- [ ] El prune de una fila `wished` elimina sus asignaciones huérfanas por cascada; una fila que permanece conserva sus categorías.
- [ ] Una operación en lote es idempotente, deduplica AppIDs y respeta el límite de 200.
- [ ] Seleccionar juegos de un grupo usa el mismo `Set<number>` y el mismo `POST /api/wishlist/package-preview` que la selección manual.
- [ ] El preview no acepta precios enviados por el cliente y mantiene subtotales independientes, faltantes y `currency = MXN`.
- [ ] `dotnet build`, `pnpm build` y las pruebas UI pasan sin introducir secretos en código, contratos o documentación.

## Siguiente paso recomendado

Implementar primero la **Fase 0**: corregir y probar la regla de `priority` en `WishlistSyncService` sin tocar todavía el esquema. Después de demostrar esa invariancia, preparar la migración de Fase 1 con una prueba aislada de las FKs compuestas y de las cascadas antes de exponer los endpoints.
