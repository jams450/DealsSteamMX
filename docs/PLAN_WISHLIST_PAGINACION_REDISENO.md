# Wishlist: paginación server-side y rediseño del gestor de categorías

> **Estado:** plan de implementación, no implementado.
>
> Este documento define la evolución de la wishlist para soportar aproximadamente 600 juegos y crecer hacia filtros por categoría, métricas de rentabilidad, cálculo real de "vale la pena" y paquetes. El objetivo es evitar que la tabla se convierta en el lugar donde viven simultáneamente consulta, administración de categorías, agregados y cálculos de precios.
>
> **Documentos relacionados:** [`PLAN_WISHLIST.md`](PLAN_WISHLIST.md), [`PLAN_WISHLIST_CATEGORIAS.md`](PLAN_WISHLIST_CATEGORIAS.md), [`PLAN_WISHLIST_PAQUETE.md`](PLAN_WISHLIST_PAQUETE.md), `Deals.Web/components/data-grid/data-grid.tsx`, `Deals.Web/app/wishlist/` y `Deals.API/Controllers/WishlistController.cs`.

## Decisión ejecutiva

La wishlist debe migrar a:

```text
DB: filtros + orden + paginación + agregados
        ↓
API paginada
        ↓
BFF
        ↓
DataGrid con manualPagination/manualSorting
        ↓
Panel independiente de categorías + tabla de juegos
```

La paginación server-side será la base. La virtualización del DOM queda como optimización posterior y solo se implementará si las mediciones demuestran que sigue siendo necesaria.

No se recomienda cambiar de componente de tabla en esta fase: `DataGrid` ya expone soporte para `manualPagination`, `manualSorting`, `rowCount`, paginación controlada y modo server.

## Problema actual

La implementación actual:

- carga la wishlist completa con `GET /api/wishlist`;
- filtra texto y categoría en `wishlist-client.tsx`;
- ordena en el cliente mediante TanStack Table;
- renderiza por fila portada, precios, descuentos, badges, sincronizaciones, fechas y acciones;
- muestra y edita categorías dentro de la tabla;
- calcula el preview del paquete en servidor, pero mantiene la selección completa en el cliente.

Con 600 filas el volumen todavía es manejable, pero el diseño actual escala mal cuando se agreguen:

- múltiples filtros de categoría;
- filtros de precios y descuentos;
- cálculo de rentabilidad por categoría;
- comparación de paquetes;
- métricas globales sobre todos los juegos;
- más columnas derivadas o acciones por fila.

El problema no es exclusivamente el número de nodos DOM. También existe trabajo innecesario de red, normalización, filtrado, ordenamiento, memoria y renderizado en cada cambio de estado.

## Alcance

### En alcance

- Paginación server-side de la wishlist.
- Búsqueda, filtros y ordenamiento server-side.
- Panel visual separado para crear, editar y eliminar categorías.
- Métricas por categoría y resumen global.
- Preservación de selección por AppID entre páginas y filtros.
- Integración con el preview de paquetes existente.
- Contratos estrictos API/BFF/frontend.
- Verificación de permisos, consultas, builds y UX.

### Fuera de alcance

- Cambiar TanStack Table por otro componente sin evidencia de necesidad.
- Virtualización como requisito inicial.
- Persistir paquetes nombrados.
- Aplicar cupones o precios nuevos desde la UI.
- Cambiar la fuente de precios existente.
- Categorías compartidas entre usuarios.
- Multiusuario de producto.
- Secretos, credenciales o valores de `.env`.

## Evidencia existente que debe reutilizarse

| Pieza | Estado / uso previsto |
|---|---|
| `DataGrid` | Ya soporta `manualPagination`, `manualSorting`, `rowCount` y estado controlado. |
| `WishlistController` | Ya obtiene la wishlist del usuario autenticado y contiene el preview de paquetes. |
| `wishlist-client.tsx` | Actualmente filtra en cliente y mantiene selección por `Set<number>`. |
| `wishlist-package.ts` | La selección usa AppID estable, no índice de fila; debe conservarse. |
| `PLAN_WISHLIST_CATEGORIAS.md` | Define persistencia, CRUD, asignaciones y autorización de categorías. |
| `PLAN_WISHLIST_PAQUETE.md` | Define el preview server-side, el límite de 200 AppIDs y la semántica de faltantes. |

## Arquitectura funcional propuesta

La pantalla se divide en cuatro responsabilidades:

```text
Wishlist
├── Resumen global de los resultados filtrados
├── Gestor de categorías
│   ├── Crear
│   ├── Renombrar
│   ├── Eliminar
│   └── Métricas
├── Filtros y orden de la tabla
└── Tabla paginada + selección + acciones rápidas
```

La tabla consulta solo la página actual. El resumen y las métricas se calculan sobre el conjunto completo que coincide con los filtros, no solo sobre las filas visibles.

## Contrato de consulta paginada

La forma exacta puede ajustarse a las convenciones existentes, pero debe cubrir como mínimo:

```http
GET /api/wishlist
  ?page=1
  &pageSize=50
  &search=...
  &categoryId=12
  &categoryState=all|none
  &sort=discountOfficial
  &direction=desc
```

Reglas:

- `page` empieza en 1.
- `pageSize` debe tener un máximo definido, por ejemplo 100.
- `search` busca por nombre y AppID.
- `categoryId` filtra una categoría propia del usuario.
- `categoryState=none` representa juegos sin categoría.
- `sort` solo acepta una whitelist de campos conocidos.
- `direction` solo acepta `asc` o `desc`.
- El usuario se resuelve desde la sesión; nunca desde query string o body.
- La consulta debe filtrar por `user_id`, `store='steam'` y `state='wished'`.
- La API debe rechazar parámetros inválidos con error tipado, no ignorarlos silenciosamente.

Respuesta propuesta:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalItems": 600,
  "totalPages": 12,
  "categories": [],
  "summary": {
    "filteredItems": 600,
    "pricedItems": 480,
    "missingPriceItems": 120
  }
}
```

La respuesta debe conservar los campos de item actuales y añadir únicamente la metadata necesaria para la paginación. No se deben enviar precios duplicados ni contratos paralelos para cada filtro.

## Filtros y ordenamiento

### Filtros iniciales

1. Texto por nombre/AppID.
2. Todas las categorías.
3. Sin categoría.
4. Una categoría individual.
5. Estado de cotización.
6. Descuento oficial mínimo.
7. Precio máximo o rango de precio, si el contrato actual permite expresarlo sin ambigüedad.

Los filtros deben poder combinarse. La semántica debe quedar documentada en el contrato y no inferirse en el frontend.

### Ordenamiento inicial

La API debe permitir únicamente campos explícitos, por ejemplo:

- nombre;
- prioridad;
- fecha de alta;
- última actualización;
- precio base;
- descuento oficial;
- descuento keys;
- score de oportunidad.

El ordenamiento debe ser determinista. Cuando dos filas empaten, usar `AppId` como desempate estable.

## Resumen y cálculo de "vale la pena"

El resumen no debe depender de la página visible. Puede entregarse dentro de la respuesta paginada en V1 o mediante un endpoint separado si el coste de consulta lo justifica:

```http
GET /api/wishlist/summary?search=...&categoryId=...
```

Debe distinguir claramente:

- cantidad total de juegos filtrados;
- juegos con precio oficial;
- juegos con precio keys;
- juegos sin cotización;
- descuento promedio y/o mediano;
- total oficial;
- total keys;
- ahorro potencial;
- cantidad que supera el umbral configurado;
- cantidad cercana al mínimo histórico.

La regla de "vale la pena" debe ser una decisión de dominio, no una condición improvisada de la UI. Una propuesta inicial:

```text
vale_la_pena =
    descuento >= minViableDiscountPercent
    OR precio_actual <= minimo_historico
    OR ahorro_absoluto >= umbral_configurado
```

Si se adoptan varias razones, la respuesta debe incluir la razón o razones que activaron la clasificación. No mostrar una etiqueta sin poder explicar su origen.

## Rediseño visual del gestor de categorías

La administración de categorías sale de la fila como responsabilidad principal y pasa a un panel propio.

### Desktop

```text
┌──────────────────────┬────────────────────────────────┐
│ Categorías           │ Wishlist                        │
│                      │ resumen + filtros              │
│ + Nueva categoría    │                                │
│ RPG                  │ tabla paginada                 │
│ Indies               │                                │
│ Sin categoría        │ paquete seleccionado           │
└──────────────────────┴────────────────────────────────┘
```

### Mobile

- Botón `Gestionar categorías`.
- Drawer o modal de pantalla completa.
- Selector de categoría separado de la administración.
- Tabla/lista debajo del panel de filtros.

### Tarjeta o fila de categoría

Cada categoría debe mostrar:

- nombre;
- cantidad de juegos;
- cantidad con precio;
- descuento promedio;
- total oficial;
- total keys;
- ahorro potencial;
- porcentaje de juegos que valen la pena.

Acciones:

```text
[Editar] [Eliminar]
```

La categoría `Sin categoría` es una vista derivada y no puede editarse ni eliminarse.

## CRUD de categorías

### Crear

```http
POST /api/wishlist/categories
Body: { "name": "RPG" }
```

Reglas:

- trim del nombre;
- longitud máxima definida por el contrato actual;
- conflicto por nombre normalizado devuelve `409`;
- la nueva categoría aparece sin recargar toda la página;
- el foco vuelve al control que abrió el formulario.

### Editar

```http
PATCH /api/wishlist/categories/{categoryId}
Body: { "name": "RPG cooperativo" }
```

Reglas:

- no cambia asignaciones;
- conserva el filtro activo si la categoría estaba seleccionada;
- actualiza badges y panel de forma coherente;
- conflicto de nombre devuelve error tipado.

### Eliminar

```http
DELETE /api/wishlist/categories/{categoryId}
```

Confirmación obligatoria:

```text
¿Eliminar “RPG”?
Los juegos no se eliminarán de tu wishlist.
Solo se quitará esta categoría y sus asignaciones.
```

La eliminación:

- no elimina juegos;
- elimina asignaciones por cascada;
- limpia el filtro si apuntaba a esa categoría;
- actualiza contadores y resumen;
- mantiene la página actual si todavía es válida.

La categoría eliminada no debe ser borrada silenciosamente mientras existan asignaciones.

## Asignación de categorías

Debe distinguirse el CRUD de categorías de la asignación de juegos.

### Acciones individuales

- `Editar categorías` desde una fila.
- Marcar o desmarcar categorías.
- Crear categoría desde el editor sin abandonar la wishlist.
- Permitir dejar un juego sin categorías.

### Acciones masivas

- `Asignar a categoría`.
- `Quitar de categoría`.
- `Reemplazar categorías`.

Las operaciones masivas reutilizan los endpoints y límites definidos en `PLAN_WISHLIST_CATEGORIAS.md`. No deben enviar más de 200 AppIDs en V1 y deben devolver `requested`, `matched`, `changed` e `ignored`.

## Selección y paquetes con paginación

La selección seguirá siendo un `Set<number>` de AppIDs.

Reglas:

- ordenar no cambia la selección;
- cambiar de página no cambia la selección;
- cambiar filtros no borra silenciosamente la selección;
- `Seleccionar visibles` solo selecciona filas de la página actual;
- el checkbox de cabecera no debe presentarse como `Seleccionar todos los resultados`;
- una futura acción `Seleccionar todos los resultados filtrados` requiere un contrato explícito, conteo server-side y confirmación.

El preview seguirá usando:

```http
POST /api/bff/wishlist/package-preview
```

con AppIDs explícitos. No se debe crear un cálculo de precios específico para cada categoría. Una categoría solo produce un conjunto de AppIDs; el endpoint existente continúa siendo la única fuente de verdad para los subtotales.

Si el servidor informa que una fila ya no pertenece a la wishlist, el cliente debe reconciliar el `Set` y mostrar un aviso no bloqueante.

## Plan de implementación por fases

### Fase 0 — Contratos y decisiones de producto

- Fijar semántica de paginación, filtros y orden.
- Fijar límite de `pageSize`.
- Definir formalmente "vale la pena".
- Definir si el resumen se incluye en la respuesta paginada o usa endpoint separado.
- Confirmar que `Sin categoría` es una vista derivada.

**Salida:** contrato revisable antes de modificar backend o UI.

### Fase 1 — API paginada y consultas

- Convertir la lectura de wishlist en consulta paginada.
- Aplicar filtros y orden en PostgreSQL mediante LINQ traducible.
- Añadir desempate determinista por AppID.
- Devolver `totalItems` y `totalPages`.
- Mantener autorización y aislamiento por usuario.
- Añadir índices si el plan de ejecución lo justifica.

**Salida:** la API no carga ni devuelve las 600 filas cuando se solicita una página de 50.

### Fase 2 — BFF y contratos frontend

- Añadir query params al BFF.
- Normalizar estrictamente metadata de paginación.
- Rechazar respuestas incompletas o inválidas.
- Mantener renovación de sesión, CSRF y errores tipados existentes.

**Salida:** el cliente puede solicitar una página concreta sin duplicar reglas de seguridad.

### Fase 3 — Integración con `DataGrid`

- Activar `manualPagination` y `manualSorting`.
- Conectar `pagination`, `onPaginationChange` y `rowCount`.
- Reiniciar a la primera página cuando cambie un filtro o búsqueda.
- Aplicar debounce al texto de búsqueda.
- Cancelar o ignorar respuestas obsoletas.
- Conservar selección global por AppID.

**Salida:** la tabla funciona con 50–100 filas sin filtrar ni ordenar las 600 en React.

### Fase 4 — Panel CRUD de categorías

- Extraer la gestión de categorías fuera de la tabla.
- Implementar panel desktop y drawer/modal mobile.
- Crear, editar y eliminar categorías.
- Mantener filtros, foco, feedback y estados de carga.
- Mantener edición rápida por fila como acción secundaria.

**Salida:** las categorías se administran sin sobrecargar cada fila ni recargar toda la wishlist.

### Fase 5 — Resumen y rentabilidad

- Añadir resumen del conjunto filtrado.
- Añadir métricas por categoría.
- Implementar la regla de "vale la pena".
- Mostrar explicación de la clasificación.
- Diferenciar precios oficiales, keys y faltantes.

**Salida:** las métricas reflejan el conjunto completo filtrado, no solo la página visible.

### Fase 6 — Paquetes y selección global

- Mantener el preview actual y su límite de 200 AppIDs.
- Verificar selección entre páginas y filtros.
- Añadir selección por categoría como atajo de AppIDs.
- Evaluar posteriormente `Seleccionar todos los resultados filtrados`.

**Salida:** paquetes correctos sin duplicar lógica de precios.

### Fase 7 — Medición y optimización final

Medir antes de introducir virtualización:

- tamaño de respuesta;
- tiempo de consulta SQL;
- tiempo de respuesta API/BFF;
- tiempo de render inicial;
- duración al cambiar filtro;
- commits React;
- memoria;
- duración del cálculo de paquete.

Solo si el resultado continúa siendo insuficiente evaluar:

- `memo` por fila;
- callbacks y columnas estables;
- imágenes lazy;
- virtualización del cuerpo de la tabla.

## Seguridad y consistencia

- Todos los endpoints resuelven el usuario desde la identidad autenticada.
- Nunca aceptar `userId` como autoridad.
- Todas las lecturas y escrituras filtran `user_id`, `store='steam'` y `state='wished'` cuando corresponda.
- No revelar existencia de AppIDs de otra cuenta.
- No aceptar precios, subtotales o métricas calculadas por el cliente como fuente de verdad.
- Conservar CSRF, sesión BFF y errores tipados.
- No incluir secretos, credenciales, tokens ni valores de `.env` en logs, respuestas o documentación.

## Riesgos y mitigaciones

| Riesgo | Mitigación |
|---|---|
| La API sigue cargando toda la wishlist | Verificar SQL generado, payload y `rowCount`; no aceptar paginación solo visual. |
| El filtro de categoría se calcula en React | El contrato debe enviar `categoryId`/`categoryState` y la consulta debe aplicar el filtro en DB. |
| Métricas muestran solo la página actual | Separar resumen global del modelo de filas paginadas. |
| Se pierde la selección al cambiar de página | Mantener `Set<number>` por AppID; nunca usar índice de fila. |
| El checkbox selecciona más de lo que el usuario ve | V1 solo selecciona visibles; etiquetar la acción explícitamente. |
| Se mezclan CRUD y asignaciones | Panel para categorías; tabla para asignación rápida. |
| Eliminar categoría elimina juegos | Cascada solo sobre asignaciones; confirmación explícita. |
| Orden no determinista | Añadir AppID como desempate. |
| Virtualización complica modales y accesibilidad | Posponerla hasta tener mediciones y pruebas de teclado/foco. |
| Cálculo de paquete usa precios stale | Mantener preview server-side existente y `pricedAt`. |

## Criterios de aceptación

- [ ] Una petición de página de 50 no devuelve los 600 juegos.
- [ ] Búsqueda, categoría, estado de precio y ordenamiento se aplican en servidor.
- [ ] La respuesta incluye `totalItems` y `totalPages` correctos.
- [ ] El ordenamiento es determinista.
- [ ] Cambiar filtro reinicia la página a la primera sin perder selección global.
- [ ] La selección se identifica por AppID y sobrevive a ordenamiento y paginación.
- [ ] Crear, renombrar y eliminar categorías funciona sin recargar toda la página.
- [ ] Eliminar una categoría no elimina juegos de la wishlist.
- [ ] La categoría `Sin categoría` no se puede editar ni eliminar.
- [ ] Las métricas por categoría representan todos los resultados filtrados.
- [ ] La regla de "vale la pena" está documentada y explicada en la respuesta.
- [ ] El preview de paquetes sigue usando el endpoint server-side existente.
- [ ] No se acepta ningún precio o subtotal enviado por el cliente.
- [ ] Se verifican autorización, pertenencia, sesión expirada y conflictos de nombres.
- [ ] `dotnet build Deals.sln` pasa.
- [ ] `cd Deals.Web && pnpm build` pasa.
- [ ] `node --test app/wishlist/_lib/` pasa.
- [ ] Se ejecuta una prueba manual con más de una categoría y más de una página.

## Verificación recomendada

```bash
dotnet build Deals.sln
cd Deals.Web
pnpm build
node --test app/wishlist/_lib/
```

Además, se debe verificar con una cuenta de prueba:

1. Crear dos categorías.
2. Asignar juegos a ambas.
3. Filtrar por cada categoría y por `Sin categoría`.
4. Cambiar de página y confirmar que la selección permanece.
5. Renombrar la categoría activa.
6. Eliminar una categoría con asignaciones.
7. Calcular un paquete con juegos de varias páginas.
8. Confirmar que el resumen no cambia artificialmente al cambiar de página.

## Resultado esperado

La wishlist debe poder crecer más allá de 600 juegos sin convertir cada interacción en un procesamiento completo del cliente. La tabla consulta únicamente lo que necesita, el panel de categorías administra el dominio de forma independiente y los cálculos de rentabilidad y paquetes permanecen centralizados en servidor.
