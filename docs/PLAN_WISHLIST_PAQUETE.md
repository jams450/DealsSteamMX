# Wishlist: suma de paquete en dos escenarios (oficial vs. keys)

> Estado: **implementado** (verificación en vivo pendiente) · Referencia base: `docs/PLAN_WISHLIST.md` · No modifica contratos existentes.

La tabla de wishlist permite seleccionar varios juegos y muestra dos subtotales independientes del paquete: escenario oficial y escenario keys/keyshops. Ambos subtotales se calculan en el servidor en minor units MXN, con conteos de cotizados y faltantes por separado. Los dos escenarios nunca se suman entre sí.

## Estado de implementación

Aplicado el 2026-09-25. Verificado: `dotnet build Deals.sln` (0 errores, 0 avisos), `pnpm build` (registra la ruta `/api/bff/wishlist/package-preview`), `node --test app/wishlist/_lib/` (28 pruebas, 8 nuevas) y `pnpm lint` sin avisos nuevos. **Pendiente:** smoke en vivo contra una API con PostgreSQL — el checkout no arranca sin `.env`, así que la aritmética del servidor y la ruta BFF todavía no han visto una petición real.

| Pieza | Dónde |
|---|---|
| Request y response del preview | `Deals.API/Models/Wishlist/WishlistModels.cs` |
| `POST /api/wishlist/package-preview` | `Deals.API/Controllers/WishlistController.cs` (`PackagePreview`, `Summarize`, `LoadItemsAsync(userId, appIds, ct)`) |
| Ruta BFF | `Deals.Web/app/api/bff/wishlist/package-preview/route.ts` |
| Contrato y normalizador | `Deals.Web/app/wishlist/_lib/wishlist-contract.ts` (`normalizeWishlistPackagePreview`) |
| Selección pura y tope | `Deals.Web/app/wishlist/_lib/wishlist-package.ts` (+ `.test.ts`) |
| Cliente y barra de paquete | `Deals.Web/app/wishlist/_lib/wishlist-api.ts`, `Deals.Web/app/wishlist/wishlist-client.tsx` |

Desviaciones deliberadas respecto del plan tal como estaba escrito:

- **La Fase 1 no se implementó aparte.** El plan preveía un estimado local provisional antes del preview; se omitió y el importe exhibido sale siempre del servidor, que es el resultado final que el propio §3 exigía. Menos código y sin el riesgo de que un número provisional se lea como definitivo.
- **La selección no se poda al cambiar filtro o página.** §3 decía que se conservaría solo lo que sigue visible; se conserva todo, porque la suma de lo que el usuario marcó no debe encogerse al filtrar sin decirlo. Sí se poda lo que el servidor reporta en `unmatchedAppIds`, y se avisa.
- **Sin botón "Calcular paquete".** Recálculo automático con debounce de 350 ms, que el plan admitía como alternativa.
- **Sin política de rate limit nueva.** Se mantiene el tope de 200 AppIDs como freno, según §9.

## Quick path

1. Agregar selección múltiple por AppID en `wishlist-client.tsx` (checkbox por fila + cabecera, barra de paquete con los dos subtotales).
2. Agregar endpoint servidor de preview que recibe AppIDs, valida pertenencia a la wishlist del usuario autenticado, relee precios actuales y devuelve los dos subtotales con conteos.
3. Verificar con `dotnet build Deals.sln` y `cd Deals.Web && pnpm build`.

## 1. Objetivo

Responder una sola pregunta de revisión de gasto: "si compro estos N juegos, ¿cuánto suma en oficial y cuánto en keys?". El usuario selecciona un subconjunto de su wishlist y ve el costo del paquete completo en dos escenarios comparables, con transparencia sobre qué juegos no tienen cotización en cada escenario.

## 2. Alcance / no alcance

| En alcance | Fuera de alcance |
|---|---|
| Selección múltiple en la tabla existente y barra de resumen del paquete | Editar precios, aplicar descuentos o cupones |
| Preview servidor con dos subtotales (oficial, keys) + conteos cotizados/faltantes | Cálculo de ahorro (diferencia oficial − keys); sigue pendiente según planes vigentes |
| Reutilizar `BestOfficialMinor` y `BestKeyshopMinor` (MXN, nullable) como únicas fuentes | Nuevos proveedores, nuevos vendedores, o desagregar keys por tienda |
| Validar que los AppIDs pertenecen a la wishlist del usuario autenticado | Compartir paquetes entre usuarios, guardar paquetes, exportar |
| Documentar límites de cantidad y tratamiento de faltantes/stale | Histórico de precios del paquete; `game_offers` sigue siendo snapshot actual, no histórico |

## 3. Decisiones de UX

| Tema | Decisión |
|---|---|
| Selección | Checkbox por fila + checkbox de cabecera (seleccionar visibles). Identidad estable por AppID, no por índice de fila. La selección sobrevive a ordenamiento; ante cambio de filtro o página se conserva solo lo que sigue visible según §6. |
| Barra de paquete | Barra fija (sticky) sobre o bajo el `DataGrid` con: conteo seleccionado, subtotal oficial, subtotal keys, conteos cotizados/faltantes por escenario, y nota "escenarios no sumables". Solo visible cuando hay ≥ 1 selección. |
| Faltantes | Cada subtotal muestra `n de N cotizados`; si hay faltantes, etiqueta explícita "sin cotizar" junto al subtotal afectado. Nunca mostrar 0 como si fuera precio. |
| Acción | Botón "Calcular paquete" (o recálculo automático con debounce). El cálculo siempre consulta al servidor; el cliente no suma precios localmente como resultado final. |
| Accesibilidad | Checkboxes con `aria-label` por juego ("Seleccionar {título}"), cabecera con `aria-label` ("Seleccionar juegos visibles"), barra de resumen como región `aria-live="polite"`, foco visible, operable solo con teclado, contraste según `Deals.Web/DESIGN.md` y `THEME_COLORS.md`. |

## 4. Contrato de respuesta (preview del paquete)

Endpoint servidor como fuente de verdad (recomendación en §5). Forma propuesta, sin alterar `WishlistResponse` ni sus items:

Solicitud (AppIDs explícitos, sin precios del cliente):

```json
{ "appIds": [1057800, 292030] }
```

Respuesta:

| Campo | Tipo | Significado |
|---|---|---|
| `selectedCount` | int | AppIDs válidos de la wishlist del usuario (tras intersección) |
| `officialSubtotalMinor` | long? | Suma de `BestOfficialMinor` de los cotizados; `null` si ninguno cotiza |
| `officialQuoted` / `officialMissing` | int | Cotizados vs. sin cotización oficial |
| `officialMissingAppIds` | int[] | AppIDs sin `BestOfficialMinor` |
| `keyshopSubtotalMinor` | long? | Suma de `BestKeyshopMinor` de los cotizados; `null` si ninguno cotiza |
| `keyshopQuoted` / `keyshopMissing` | int | Cotizados vs. sin cotización keys |
| `keyshopMissingAppIds` | int[] | AppIDs sin `BestKeyshopMinor` |
| `currency` | string | Siempre `"MXN"` |
| `pricedAt` | string (UTC ISO) | Momento de lectura de precios; base de "stale" en §7 |

Ejemplo:

```json
{
  "selectedCount": 3,
  "officialSubtotalMinor": 129997,
  "officialQuoted": 2,
  "officialMissing": 1,
  "officialMissingAppIds": [292030],
  "keyshopSubtotalMinor": 8997,
  "keyshopQuoted": 1,
  "keyshopMissing": 2,
  "keyshopMissingAppIds": [1057800, 292030],
  "currency": "MXN",
  "pricedAt": "2026-09-25T00:00:00Z"
}
```

Reglas de lectura obligatorias para el frontend: pintar cada subtotal solo con su propio conteo; mostrar "sin cotizar" cuando el subtotal es `null` o `missing > 0`; jamás sumar `officialSubtotalMinor + keyshopSubtotalMinor` ni promediarlos. La UI lo refuerza con la nota "Son dos escenarios alternativos, no dos parciales de un total".

## 5. POST de preview vs. cálculo local

| Opción | Ventaja | Riesgo |
|---|---|---|
| **A. POST preview en servidor (recomendada)** | Fuente de verdad: relee `BestOfficialMinor`/`BestKeyshopMinor` actuales, ignora precios del cliente, aplica pertenencia y límites en un solo punto | Una petición más por cálculo; requiere nueva ruta backend + BFF |
| B. Suma local en el cliente | Sin backend nuevo; instantánea | Usa filas potencialmente stale, duplica reglas de nulos, confía en datos ya entregados; rompe la regla "no confiar en precios enviados por cliente" |

Recomendación: **opción A**. El cliente puede mostrar un estimado optimista con los valores ya cargados en la tabla, pero el valor exhibido como "paquete" debe venir del preview servidor. Esto mantiene coherencia con el modelo existente (BFF bajo `/api/bff/wishlist`, API bajo `/api/wishlist`) y deja el control de seguridad y límites en el servidor.

## 6. Diseño

### 6.1 Backend (`.NET 9`)

- Nueva acción POST (p. ej. `POST /api/wishlist/package-preview`) en el área de `WishlistController`. Recibe `{ appIds: int[] }`.
- Flujo: obtener usuario autenticado del contexto (misma política que el resto del controlador) → cargar su wishlist → intersectar AppIDs solicitados con los propios → releer `BestOfficialMinor` y `BestKeyshopMinor` actuales por juego → sumar por escenario según §6.4 → devolver contrato de §4.
- No aceptar precios, subtotales ni moneda desde el cliente. Devolver `400` si la lista viene vacía o excede el límite (§9); ignorar silenciosamente (o reportar como no seleccionados) los AppIDs que no pertenecen al usuario, sin filtrar información de otros usuarios.
- Sin migración: no hay tabla de paquetes; el preview es stateless y se calcula sobre la wishlist y los derivados de `game_offers` ya existentes.

### 6.2 BFF (`Deals.Web`)

- Nueva ruta bajo `/api/bff/wishlist/` (p. ej. `package-preview/route.ts`) que replica el patrón existente: `getServerSession()`, `fetchApiWithAutoRefresh`, `attachSessionCookie`, errores tipados `{ code, message, traceId }`.
- Valida forma mínima (arreglo de enteros positivos, tope de cantidad) antes de proxear; no transforma precios.

### 6.3 Frontend (`Deals.Web`)

- Estado de selección en `wishlist-client.tsx`: `Set<number>` por AppID. Columnas existentes de `bestOfficialMinor` y `bestKeyshopMinor` no cambian; se agrega columna de selección con checkbox.
- Cliente API en `wishlist-api.ts`: función `previewPackage(appIds: number[])` con `csrfFetch` + `parseApiError`, coherente con el contrato en `wishlist-contract.ts` (nuevo tipo `PackagePreview`, sin tocar los tipos de item existentes).
- Barra de paquete: dispara el preview al cambiar la selección (debounce ~300 ms) o con botón explícito; muestra skeleton/error por separado del grid; ante 401 aplica el flujo de sesión existente.

### 6.4 Selección estable, filtros y paginación

| Caso | Comportamiento |
|---|---|
| Ordenamiento | La selección no cambia (clave AppID). |
| Filtro aplicado | "Seleccionar visibles" afecta solo a filas filtradas; la barra indica "N seleccionados (filtro activo)". |
| Cambio de página | Opción simple v1: la selección persiste por AppID en todas las páginas cargadas; el preview envía todos los AppIDs seleccionados aunque no estén en la página visible. Alternativa documentada (no recomendada v1): "seleccionar todo lo filtrado" masivo, que exige conteo servidor y confirma explícitamente; queda fuera de v1. |
| Juego eliminado de la wishlist entre selección y preview | El servidor lo excluye de `selectedCount`; el cliente concilia quitándolo del set y avisa con mensaje no bloqueante. |

## 7. Suma, moneda, faltantes y staleness

- **Minor units, suma segura**: sumar `long` (Int64) en minor units MXN; cada sumando es `int?` no negativo. Validar `sumando >= 0`; cualquier negativo se trata como dato corrupto: se excluye y se cuenta como faltante (más log servidor). Verificar desbordamiento con suma en `checked` o validación previa contra el tope (§9).
- **Moneda**: solo MXN. El contrato devuelve `currency: "MXN"` fijo; el frontend formatea con `Intl.NumberFormat("es-MX", { style: "currency", currency: "MXN" })` dividiendo entre 100. No hay conversión en este flujo.
- **Regla de precios**: escenario oficial = mejor oferta oficial actual por juego (`BestOfficialMinor`); escenario keys = agregado keyshop (`BestKeyshopMinor`). No mezclar fuentes ni inventar vendedores.
- **Nulos**: `null` = no cotizado. Se excluye de la suma, incrementa `missing` y aparece en `missingAppIds`. Un escenario sin ningún cotizado devuelve subtotal `null` (no 0).
- **Stale / snapshot**: `game_offers` es snapshot actual (`mxn_current_price_minor`), no histórico. El preview relee el estado corriente y sella `pricedAt`. Si entre la carga de la tabla y el preview los precios cambiaron, el preview manda: el cliente muestra nota "precios actualizados al {hora}" y refresca las filas visibles cuando sea barato hacerlo. No se congela inventario ni se reserva precio: concurrencia de último escritor gana, documentado como tal.

## 8. Seguridad

- Autenticación con la misma política del controlador (`AdminWithId` en esta app single-admin); el servicio resuelve el usuario desde el contexto, nunca desde un `userId` del body.
- Autorización por pertenencia: intersección estricta contra la wishlist propia. No devolver precios ni existencia de AppIDs ajenos.
- Sin precios del cliente: cualquier `price`/`subtotal` enviado se ignora.
- CSRF y sesión en BFF según el modelo existente (doble-submit + `x-csrf-token`, cookie httpOnly, refresh con rotación). Sin secretos nuevos ni en el documento ni en el código.

## 9. Límites de cantidad y overflow

| Límite | Valor propuesto | Motivo |
|---|---|---|
| AppIDs por preview | máx. 200 | Acota payload y trabajo por petición; por encima se devuelve `400` con código `BAD_REQUEST` |
| Deduplicación | `Distinct()` antes de procesar | Evita doble conteo por error del cliente |
| Tope de suma | `long` con `checked`; rechazar sumando > `999_999_999_00` minor units (~999M MXN) como dato corrupto | Impide overflow y absorbe precios anómalos sin romper el preview |
| Rate limit | Reutilizar la política del área de wishlist si existe; si no, sin política nueva en v1 pero con el tope de 200 como freno | v1 no necesita ventana propia; registrar si aparece abuso |

## 10. Fases ordenadas

1. **Fase 1 — Selección + barra con estimado local marcado como provisional.** Checkbox por AppID, barra con conteos, sin endpoint nuevo. Criterio de salida: selección estable ante orden/filtro/página y etiquetas de accesibilidad presentes.
2. **Fase 2 — Preview servidor + BFF.** Endpoint, intersección por pertenencia, contrato de §4, cliente `previewPackage`. Criterio de salida: la barra muestra valores servidor con `pricedAt` y faltantes por escenario.
3. **Fase 3 — Endurecimiento.** Límites de §9, conciliación de eliminados, nota de stale, `aria-live`, pruebas manuales de aceptación (§12). Sin migración en ninguna fase.

## 11. Migraciones

Preferir **ninguna**. El preview es stateless; no se persiste el paquete ni sus subtotales. Si a futuro se guardan paquetes nombrados, eso es otro documento y otra migración.

## 12. Riesgos

| Riesgo | Mitigación |
|---|---|
| Leer la barra como "total a pagar" sumando ambos escenarios | Nota fija "escenarios alternativos", subtotales en tarjetas separadas, contrato sin campo de total combinado |
| Nulos interpretados como 0 o como "gratis" | Subtotal `null` + etiqueta "sin cotizar" + `missingAppIds` visibles al inspeccionar |
| Filas stale en el estimado local | El valor oficial de la barra siempre es servidor con `pricedAt`; el estimado local se etiqueta "provisional" o se omite en v2 |
| Selección masiva accidental ("todo lo filtrado") | v1 solo selecciona visibles/explícitos; el "seleccionar todo" queda fuera hasta tener conteo servidor |
| Precio anómalo que distorsiona la suma | Tope por sumando + suma `checked` + conteo como faltante con log |

## 13. Criterios de aceptación

- [ ] Seleccionar 3 juegos con cotización en ambos escenarios muestra dos subtotales iguales a la suma manual de `BestOfficialMinor` y `BestKeyshopMinor` releídos en servidor.
- [ ] Un juego sin `BestKeyshopMinor` aparece en `keyshopMissingAppIds`, no altera `keyshopSubtotalMinor`, y la UI lo marca "sin cotizar".
- [ ] Un escenario sin ningún cotizado devuelve subtotal `null` y la UI no pinta 0 ni "$0.00" como precio.
- [ ] Enviar AppIDs ajenos o inexistentes no los incluye en `selectedCount` ni filtra datos de otros usuarios.
- [ ] Enviar precios en el body no altera el resultado (se ignoran).
- [ ] Más de 200 AppIDs devuelve `400`.
- [ ] La selección sobrevive a reordenamiento y paginación (clave AppID).
- [ ] La barra declara `aria-live="polite"` y cada checkbox tiene nombre accesible.
- [ ] Ningún texto suma ambos escenarios; `currency` es siempre `"MXN"`.

## 14. Verificación

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```

Sin test project en el repo: verificación mínima = build de ambas piezas + smoke manual (login admin, abrir wishlist, seleccionar 2–3 juegos, comparar barra contra suma de columnas, forzar un faltante y un 401 de sesión). No incluir secretos en reportes ni en el contrato.
