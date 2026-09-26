# DealExt: plan de reconciliación canónica entre estados y tiendas

Estado: **implementación parcial verificada en código**. La reconciliación read-only, contrato BFF, pantalla de revisión y detalle versionado de merge están implementados; ejecución de fusión debe seguir endureciéndose antes de producción. Aplicación de migración y smoke de base siguen pendientes.

Objetivo: permitir que mantenimiento encuentre candidatos cuando una misma obra aparece en estados distintos —por ejemplo, Steam `wished` y GOG `owned`— y que una fusión manual conserve todas las filas de biblioteca, sus estados y sus tiendas bajo una sola identidad canónica.

> **Regla central:** el título solo propone candidatos. Nunca prueba identidad, propiedad, estado ni equivalencia de edición; nunca dispara una fusión automática.

## 1. Problema verificado y resultado deseado

### Comportamiento actual

- `GameMergeService.FindDuplicateGroupsAsync` consulta `user_library` solo para el usuario administrador actual y filtra `state IN ('owned','subscription')`.
- Por tanto, una fila Steam `wished` no puede formar grupo con una fila GOG `owned`, aunque ambas apunten a juegos canónicos distintos.
- El agrupamiento usa un *fold* calculado sobre `games.title`; no usa `games.normalized_title`.
- `MergeAsync` repunta `user_library.game_id` al superviviente sin cambiar `state`, `store` ni `store_game_id`. También mueve ids externos, `steam_games`, ofertas, reseñas y favoritos dentro de una transacción y registra `game_merges`.
- La fusión actual bloquea el par si entre ambos juegos existen appids Steam distintos, porque el binding de precios quedaría ambiguo.
- La UI `/library/duplicates` ya permite seleccionar varios grupos y ejecuta fusiones secuenciales mediante el endpoint existente por juego absorbido.
- `ownedStores` se calcula sobre filas del `game_id` canónico con `state='owned'` y excluye `store='steam'`; `subscription` produce el indicador de suscripción, no propiedad.

### Resultado deseado

1. Detectar candidatos cross-state usando todas las filas relevantes del usuario: `wished`, `owned` y `subscription`.
2. Mostrar evidencia, confianza y razones suficientes para que una persona decida.
3. Permitir seleccionar un superviviente y varios absorbidos, con confirmación explícita.
4. Ejecutar la fusión existente o su extensión sin reinterpretar ninguna fila:
   - `wished` sigue siendo `wished`.
   - `owned` sigue siendo `owned`.
   - `subscription` sigue siendo `subscription`.
   - `store` y `store_game_id` siguen siendo los ids de sus tiendas.
5. Tras la fusión, `ownedStores` debe incluir las tiendas no Steam de las filas `owned` ahora asociadas al superviviente.

## 2. Alcance

### Incluido

- Nuevo endpoint de candidatos cross-state, de solo lectura y protegido con `AdminWithId`.
- DTOs con miembros, estados, tiendas, ids externos, appids Steam, tipo/edición cuando estén disponibles, confianza y razones.
- Reglas de evidencia y bloqueo para juegos, DLC, ediciones y appids Steam conflictivos.
- Flujo UI de revisión, selección múltiple y confirmación humana.
- Invariantes de merge, colisiones, idempotencia, auditoría y rollback operativo.
- Tests de backend, contratos BFF y UI.

### No incluido

- Auto-merge por título, similitud o coincidencia de estado.
- Afirmar que una fila `owned` demuestra que otra fila del mismo título es propiedad del usuario.
- Resolver automáticamente DLC, demos, bandas sonoras, paquetes, remasters o ediciones completas.
- Cambiar estados importados, convertir `subscription` en `owned` o fusionar filas de tiendas.
- Reemplazar la identidad exacta por `normalized_title`.
- Añadir proveedor externo nuevo, scraping o una API de catálogo no necesaria para presentar candidatos.
- Rediseñar `games`, `game_external_ids`, `user_library` o el esquema de ofertas si las restricciones actuales bastan.

## 3. Identidad canónica y jerarquía de evidencia

`games.game_id` es una clave sustituta. La identidad durable está en `game_external_ids(namespace, external_id)`, con unicidad global por namespace e id. `games.normalized_title` es índice de búsqueda y evidencia débil, nunca clave natural.

Orden recomendado de evidencia, de más fuerte a más débil:

| Nivel | Evidencia | Uso | ¿Permite auto-fusión? |
|---|---|---|---|
| 1 | Mismo `(namespace, external_id)` exacto | Ya es la misma identidad; no es candidato de merge | No aplica |
| 2 | UUID ITAD exacto que cruza Steam/GOG u otra tienda | Candidato de confianza alta; mostrar origen y ids | No |
| 3 | Ids externos cross-store verificados por una fuente confiable | Candidato de confianza alta/media, sujeto a tipo y edición | No |
| 4 | Mismo appid Steam exacto, o mismo id de tienda exacto | Señala identidad compartida si las filas no están ya unificadas | No; investigar integridad antes |
| 5 | `games.normalized_title`/fold equivalente, mismo tipo y año compatible | Candidato heurístico | No |
| 6 | Título parecido, sin evidencia de tienda o fecha | Solo revisión exploratoria | No |

Reglas:

- La coincidencia de título **solo propone candidatos**. No prueba identidad, ownership ni equivalencia de edición.
- `normalized_title` puede eliminar sufijos de edición; por eso debe acompañarse de tipo, año, ids y señales de DLC/edición.
- Un candidato debe explicar qué evidencia lo produjo y qué incertidumbres permanecen.
- No presentar `owned` en un miembro como evidencia de que el usuario posee el juego en otra tienda.
- Si una fuente exacta contradice título, tipo o edición, prevalece la alerta: no ocultar el conflicto ni permitir confirmación ambigua.

## 4. Endpoint de candidatos cross-state

### Contrato propuesto

Añadir, manteniendo `GET /api/games/merge-suggestions` compatible para la pantalla actual, un endpoint explícito:

```text
GET /api/games/cross-state-reconciliation
```

Alternativa aceptable si no se desea otra ruta: versionar o ampliar `merge-suggestions` con un parámetro explícito `scope=cross-state`; no mezclar silenciosamente el resultado nuevo con el contrato actual.

Características:

- `GET`, `AdminWithId`, `cache-control: no-store` en BFF.
- Alcance limitado a la biblioteca del usuario autenticado, no a todos los usuarios.
- Lectura proyectada; no crea juegos, ids ni filas de biblioteca.
- Incluye `wished`, `owned` y `subscription`; documenta que otros estados futuros quedan fuera hasta añadirlos a una allowlist.
- Excluye grupos donde solo exista un `game_id` canónico.
- Agrupa candidatos por relación explicable, no por una simple igualdad de título sin metadatos.
- Devuelve bloqueos por grupo y por miembro, sin convertir un bloqueo en ausencia de candidato.

### DTO sugerido

Nombres orientativos, a alinear con las convenciones actuales de `Deals.API/Models/Games/GameMergeModels.cs`:

```text
CrossStateCandidateGroupResponse
  candidateKey
  confidence: high | medium | low
  reasons[]
  warnings[]
  blocked
  blockReason
  members[]

CrossStateCandidateMemberResponse
  gameId
  title
  normalizedTitle
  type
  releaseYear
  states[]              // wished | owned | subscription
  storeRows[]           // store, storeGameId, state, title
  externalIds[]         // namespace, externalId
  steamAppIds[]
  evidence[]
  blocked
  blockReason
```

El contrato real debe evitar devolver datos innecesarios de usuario. No incluir credenciales, payloads de proveedores ni secretos. El BFF debe normalizar y acotar tamaños como hace `games-merge.ts`; una respuesta inválida es error upstream, no lista vacía.

### Consulta y agrupamiento

- Recuperar filas `user_library` del usuario con `game_id` no nulo y estados permitidos.
- Unir `games`, `game_external_ids` y, cuando proceda, `steam_games`/`game_offers` solo para evidencia y bloqueo.
- Consultar `games.normalized_title` como primera clave indexada; calcular el fold compatible con el detector actual solo para preservar casos históricos y explicar la diferencia.
- No usar `user_library.title` como identidad cuando existe `games.title`; mostrar ambos solo si ayudan a explicar una discrepancia.
- Evitar N+1: una consulta proyectada por lote, agrupamiento en memoria acotado o SQL con agregación estable.

## 5. Confianza, razones y bloqueos

La confianza es explicativa, no una autorización automática:

| Confianza | Condición orientativa | Presentación |
|---|---|---|
| Alta | Id externo cross-store exacto y tipo compatible; o evidencia exacta equivalente sin conflicto | “Evidencia fuerte; confirmar fusión” |
| Media | Título normalizado igual más año/tipo compatibles y al menos una señal de tienda | “Coincidencia probable; revisar edición” |
| Baja | Solo título/fold, o metadatos incompletos | “Coincidencia débil; revisar manualmente” |

Razones deben ser datos estructurados y texto legible, por ejemplo:

- `sameItadId`: “Mismo UUID ITAD en Steam y GOG”.
- `sameNormalizedTitle`: “Mismo título normalizado; no demuestra identidad”.
- `crossState`: “Una fila está wished y otra owned”.
- `releaseYearCompatible`: “Años compatibles dentro de la tolerancia definida”.
- `editionWarning`: “El título puede ser edición/remaster”.
- `dlcWarning`: “Uno o más miembros parecen DLC o contenido adicional”.
- `steamAppIdConflict`: “Appids Steam distintos; la fusión dejaría precio ambiguo”.

### Juegos, DLC y ediciones

Bloquear o exigir revisión reforzada cuando:

- `type` difiera entre `game`, `dlc`, `demo`, `soundtrack`, `bundle` u otro valor conocido.
- Un título contenga indicadores de edición (`GOTY`, `Deluxe`, `Complete`, `Definitive`, `Remastered`, etc.) que el normalizador haya eliminado.
- Existan años de lanzamiento incompatibles o fechas que indiquen obras distintas.
- Una fuente identifique un DLC y otra un juego base.

No basta con quitar sufijos para permitir la fusión. Si el sistema no tiene evidencia suficiente, mostrar el grupo como revisable pero bloqueado, con opción de no hacer nada.

### Appids Steam conflictivos

- Un canonical puede tener como máximo un appid Steam efectivo para el binding actual.
- Si el par o grupo contiene appids distintos, el endpoint debe marcarlo bloqueado y explicar el motivo.
- No resolver el conflicto eligiendo `MIN(app_id)`, el survivor elegido por el usuario ni el título “más parecido”.
- Solo una operación posterior, explícita y diseñada para separar/reasignar identidad Steam podría desbloquearlo; queda fuera de este plan.

## 6. Confirmación humana y flujo multi-select

La UI existente `/library/duplicates` puede evolucionar o compartir componentes con una vista `/library/reconciliation`.

Flujo:

1. Cargar candidatos con `GET` y mostrar filtros por confianza, estado y tienda.
2. Seleccionar uno o varios grupos independientes.
3. Dentro de cada grupo, seleccionar exactamente un superviviente; mostrar estados y tiendas de cada miembro.
4. Seleccionar uno o varios absorbidos. No permitir que un juego sea simultáneamente survivor y absorbed.
5. Mostrar resumen previo: filas `wished`, `owned`, `subscription`, tiendas, appids Steam, ids externos, reviews, favorites y posibles bloqueos.
6. Requerir confirmación explícita para cada grupo o para el lote completo con listado completo de operaciones.
7. Ejecutar secuencialmente el endpoint existente `POST /api/games/{absorbedGameId}/merge` con `{ intoGameId }`, o un endpoint batch futuro que conserve transacciones individuales.
8. Tras cada `200`, actualizar el estado local y retirar el absorbido; ante `409`, detener el lote y conservar reporte de operaciones aplicadas y pendientes.
9. Recargar candidatos al finalizar para evitar operar sobre ids ya absorbidos.

La selección múltiple no debe convertir varias fusiones en una transacción gigante: el endpoint actual ofrece aislamiento por absorbido y rollback transaccional de cada operación. El usuario debe ver qué grupos se aplicaron antes del primer error.

## 7. Invariantes de la fusión

### Estados y tiendas

`MergeAsync` debe conservar literalmente cada combinación existente:

```text
(user_id, store, store_game_id, state, is_installed, priority, added_at, imported_at, game_id)
```

Solo cambia `game_id` de absorbido a survivor. No:

- cambia `wished` a `owned`;
- cambia `owned` a `subscription`;
- deduce ownership GOG/Epic/Xbox desde una fila Steam;
- elimina una fila solo porque otra tienda tiene el mismo título;
- mezcla `store_game_id` entre tiendas;
- sobrescribe silenciosamente prioridad, instalación o fechas.

### Colisiones de `user_library`

La restricción actual es `UNIQUE (user_id, store, store_game_id, state)`. Antes de repuntar:

1. Detectar colisiones exactas entre filas del survivor y absorbido.
2. Si ambas filas representan la misma clave única, conservar una sola fila mediante regla idempotente documentada, sin cambiar su estado/tienda; preferir la fila con datos de importación más completos y registrar la deduplicación en el resultado/auditoría.
3. Si las claves difieren, repuntar ambas.
4. No colapsar estados distintos: `(gog, X, wished)` y `(gog, X, owned)` deben seguir siendo dos filas permitidas por el esquema.
5. Si la deduplicación requiere una decisión semántica no demostrable, bloquear y no escribir.

La implementación debe comprobar si la deduplicación de una colisión puede preservar datos de ambas filas (por ejemplo, `is_installed`, `priority` y fechas) antes del `UPDATE`; no usar un `UPDATE` ciego que termine en `23505`.

### Ids externos

`game_external_ids` tiene `UNIQUE (namespace, external_id)` global:

- Si el id existe solo en absorbido, moverlo al survivor.
- Si ya existe en survivor, no insertar duplicado; registrar como deduplicado/idempotente.
- Si el mismo id apunta a otro tercer `game_id`, bloquear la operación: es conflicto de identidad, no una colisión que se pueda borrar.
- No resolver conflictos eliminando el id del tercero.

### `steam_games` y ofertas

- Repetir el bloqueo para appids Steam distintos antes de cualquier escritura.
- Si dos filas `steam_games` colisionan en una clave única de snapshot, conservar el snapshot válido según una regla explícita y registrar el resultado; no escoger silenciosamente por id.
- Para `game_offers`, detectar colisiones en `(game_id, region, source, offer_key)` y en la clave histórica `(steam_game_id, source, offer_key)` donde siga vigente.
- La política de colisión debe conservar el dato más reciente según `observed_at`, con una comparación determinista y registro de filas descartadas; nunca presentar el descarte como pérdida accidental.
- Verificar también ofertas Steam-less ancladas solo a `game_id`.

### Reseñas y favoritos

- `game_reviews` no tiene unicidad por `(user_id, game_id, platform)`; mover todas las reseñas y no descartar ninguna.
- Si una futura restricción introdujera colisión, bloquear hasta definir merge de contenido; no añadir una regla implícita.
- `user_game_favorites` tiene PK `(user_id, game_id)`: si survivor y absorbido están marcados por el mismo usuario, borrar primero la fila absorbida y conservar el significado booleano de favorito; después repuntar las restantes.
- Mantener `game_merges.dropped_reviews = 0` mientras no exista descarte real.

## 8. `ownedStores` y lecturas posteriores

Después de una fusión:

- `GameOwnershipService` debe encontrar el survivor por `('steam', appid)` y consultar todas las filas del usuario con ese `game_id`.
- `ownedStores` debe ser la unión distinta de `store` para `state='owned'` excluyendo Steam.
- `subscription` solo debe activar `hasGamePass`/indicador equivalente; no debe entrar en `ownedStores`.
- Una fila Steam `wished` no debe producir `ownedStores`; una fila GOG `owned` repuntada al survivor sí debe producir `gog`.
- Si el mismo juego tiene GOG `owned` y Xbox `subscription`, el resultado es `ownedStores=['gog']` y suscripción verdadera.
- La wishlist debe seguir mostrando la entrada `wished` y sus categorías, prioridades y fechas; no debe desaparecer por existir una fila `owned` en otra tienda.

Añadir una prueba de regresión específica para evitar que una optimización futura vuelva a filtrar por `owned/subscription` y oculte `wished` durante la reconciliación.

## 9. Auditoría, rollback y seguridad operativa

### Auditoría

Extender el reporte de merge, sin secretos, con contadores y decisiones:

- filas de biblioteca repuntadas y deduplicadas;
- filas por estado y tienda antes/después;
- ids externos movidos/deduplicados;
- ofertas repuntadas/deduplicadas;
- reseñas movidas y favoritas deduplicadas;
- confianza/razones del candidato confirmado, si el endpoint de merge recibe un `candidateKey` o evidencia de UI;
- actor, survivor, absorbed y timestamp.

El snapshot actual de `game_merges` solo incluye `games` e ids externos. Para rollback fiable de filas deduplicadas o colisiones de ofertas, ampliar el snapshot o crear un detalle de merge versionado antes de modificar. No confiar únicamente en que el `game_id` absorbido pueda recrearse.

### Rollback

- Cada merge debe seguir siendo una transacción atómica: si falla una FK, unique key o validación, no queda escritura parcial.
- El rollback operativo NO está implementado: el snapshot/detalle permite identificar filas y contadores afectados, pero no existe replayer ejecutable ni endpoint de reversión.
- La UI no debe ofrecer una acción de merge ni afirmar reversión; la superficie permanece de solo lectura hasta que exista un flujo candidate-bound con confirmación y rollback operativo.
- Hacer backup/rollback previo en producción según el procedimiento existente antes de aplicar migraciones o cambios de merge.

No loguear títulos completos si se consideran datos innecesarios, payloads de proveedores, tokens, credenciales ni valores de entorno.

## 10. Archivos probablemente afectados

### Backend .NET

- `Deals.BusinessLogic/Services/GameMergeService.cs`: consulta cross-state, cálculo de evidencia/confianza, validación de tipos/ediciones, colisiones y contadores.
- `Deals.BusinessLogic/Interfaces/IGameMergeService.cs`: contrato nuevo o extensión separada para candidatos.
- `Deals.BusinessLogic/Models/Catalog/*`: reutilizar tipos existentes; añadir modelos de evidencia solo si no existe ubicación mejor.
- `Deals.API/Controllers/GamesController.cs`: endpoint GET nuevo y, si se decide, validación de candidate key en POST.
- `Deals.API/Models/Games/GameMergeModels.cs`: DTOs wire y normalización de nombres.
- `Deals.BusinessLogic/Models/Library/GameOwnership.cs` y `GameOwnershipService.cs`: principalmente pruebas/regresión de `ownedStores`; cambiar solo si la lectura actual no refleja filas repuntadas.
- Registro DI solo si se extrae un servicio de reconciliación separado.

### BFF y frontend Next.js

- `Deals.Web/lib/contracts/games-merge.ts`: contrato, normalizadores estrictos, límites y razones.
- `Deals.Web/app/api/bff/games/merge-suggestions/route.ts` o nueva ruta BFF `cross-state-reconciliation/route.ts`: sesión admin, proxy autenticado, `no-store`, errores y cookie renovada.
- `Deals.Web/app/library/duplicates/_lib/games-merge-api.ts`: cliente del endpoint y tipos.
- `Deals.Web/app/library/duplicates/duplicates-client.tsx`: estados cross-state, evidencia, bloqueos, selección de varios grupos y confirmación.
- Nueva página solo si la experiencia no cabe en `/library/duplicates`; si se crea, respetar middleware global y navegación existente.
- Tests de `Deals.Web/lib/contracts/games-merge.test.ts` y tests de componentes/cliente disponibles.

## 11. Base de datos y migraciones

**No se necesita una migración para detectar estados ni para repuntar `user_library.game_id`:** el esquema actual ya contiene `games`, `game_external_ids`, `user_library.game_id`, `game_offers.game_id` y `game_merges`.

Antes de implementar, verificar en la base objetivo:

- restricciones y claves únicas reales, incluidas migraciones aplicadas después de `SQL/schema.sql`;
- FKs y acciones `ON DELETE` de todos los referrers;
- si existe alguna restricción de unicidad adicional en ofertas, reseñas o snapshots;
- índices suficientes para `user_library(user_id, game_id, state)`, `games.normalized_title` y `game_external_ids(namespace, external_id)`.

Solo crear migración si la auditoría demuestra una carencia concreta, por ejemplo:

- índice compuesto necesario para la consulta cross-state;
- tabla de detalle de merge para rollback y deduplicaciones;
- constraint/índice que el código actual ya presupone pero la base aplicada no tiene.

No añadir `UNIQUE games.normalized_title`: rompería la finalidad de reconciliar duplicados y convertiría una heurística en identidad.

## 12. Matriz de pruebas

| Caso | Preparación | Resultado esperado |
|---|---|---|
| Steam `wished` + GOG `owned`, títulos iguales | Dos `game_id`, estados distintos | Candidato visible; ninguna fusión automática |
| Steam `wished` + GOG `owned`, títulos distintos pero mismo UUID ITAD | Id externo cross-store exacto | Confianza alta, razón explícita, confirmación requerida |
| Dos títulos iguales sin id externo | Dos juegos canónicos | Confianza baja/media; nunca auto-merge |
| `owned` + `subscription` mismo juego | Filas distintas o estados distintos | Estados intactos; `ownedStores` solo owned |
| Tres tiendas y tres estados | Steam wished, GOG owned, Xbox subscription | Una identidad survivor con las tres filas, sin reinterpretación |
| Mismo `(store, store_game_id, state)` en ambos juegos | Colisión de unique key | Deduplicación determinista o bloqueo; nunca `23505` parcial |
| Estados distintos con mismo store/id | `wished` y `owned` | Ambas filas sobreviven |
| Id externo duplicado en survivor/absorbed | Mismo namespace/id | Una referencia, resultado auditado, merge idempotente |
| Id externo apunta a tercer juego | Conflicto global | `409`, cero escrituras |
| Appids Steam distintos | Dos ids Steam | Grupo/merge bloqueado, cero escrituras |
| DLC frente a juego base | Tipos o señales incompatibles | Advertencia fuerte/bloqueo según regla; no auto-merge |
| Edición/remaster | Sufijo eliminado por normalización | Advertencia visible; confirmación reforzada o bloqueo |
| Reseñas en ambos juegos | Varias plataformas/usuarios | Todas se mueven, `dropped_reviews=0` |
| Favorito duplicado | Mismo usuario marca ambos | Una fila favorita permanece |
| Ofertas en colisión | Mismo game/region/source/key | Regla de frescura determinista y contador auditado |
| Fusión repetida | Absorbido ya eliminado | `404`/resultado seguro, sin cambios adicionales |
| Dos merges concurrentes | Mismos juegos en orden inverso | Lock/orden evita deadlock y una operación no corrompe la otra |
| Candidato stale en UI | Otro admin fusiona primero | Error controlado, recarga de candidatos |
| Lote multi-grupo con fallo intermedio | Segundo grupo bloqueado | Primer grupo queda aplicado; reporte separa aplicado/pendiente |
| `ownedStores` posterior | GOG owned repuntado a survivor | Aparece GOG; Steam sigue excluido |
| Wishlist posterior | Steam wished repuntado | Sigue en wishlist con prioridad/categoría |

## 13. Verificación y smoke test

### Verificación estática y automatizada

Ejecutar, cuando exista implementación:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
node --test Deals.Web/lib/contracts/games-merge.test.ts
```

Añadir pruebas backend para SQL/proyección y merge en una base PostgreSQL de prueba; el repositorio no tiene actualmente proyectos de tests, por lo que debe definirse el harness antes de afirmar cobertura.

### Smoke test en entorno controlado

Precondiciones:

- base de datos respaldada y procedimiento de rollback disponible;
- usuario administrador de prueba;
- dataset controlado con dos o tres `game_id` y filas de estados/tiendas conocidas;
- sin exponer secretos en comandos, logs ni documentación.

Secuencia:

1. Consultar y guardar conteos de `user_library` por `(game_id, state, store)`.
2. Llamar al endpoint de candidatos y comprobar razones, estados y bloqueos.
3. Confirmar una fusión Steam wished + GOG owned.
4. Consultar nuevamente filas, wishlist, biblioteca, reviews, favorites, ofertas y `ownedStores`.
5. Ejecutar un caso bloqueado por appids Steam distintos y comprobar cero escrituras.
6. Repetir un caso con colisión de unique key y comprobar deduplicación/rollback.
7. Verificar `game_merges` y el snapshot/detalle de auditoría.

## 14. Ejemplo Cyberpunk: 115 / 2940 / 316

Escenario de aceptación: el mantenimiento muestra tres juegos canónicos separados, `game_id` **115**, **2940** y **316**, asociados a representaciones de Cyberpunk en Steam/GOG y posiblemente más de una fila o edición. Una combinación relevante contiene Steam `wished` y GOG `owned` bajo ids distintos.

Resultado esperado:

- Los tres ids pueden aparecer como miembros del mismo conjunto de candidatos solo si cada relación tiene evidencia y advertencias visibles; no basta con el texto “Cyberpunk”.
- Si los appids Steam de los miembros son distintos, el grupo queda bloqueado para merge hasta resolver la identidad Steam.
- Si existe un único appid Steam compatible y la persona confirma que son la misma obra/edición, se elige un survivor explícito —por ejemplo `115`— y se absorben los otros ids mediante operaciones transaccionales.
- La fila Steam conserva `state='wished'`, `store='steam'`, `store_game_id` y prioridad.
- La fila GOG conserva `state='owned'`, `store='gog'` y su `store_game_id`.
- Si `316` contiene `subscription`, conserva ese estado; no se convierte en owned.
- Tras la fusión, las tres evidencias quedan bajo el survivor, `ownedStores` devuelve GOG si esa fila es `owned`, Steam sigue excluido y la wishlist conserva la entrada wished.
- No se afirma “el usuario posee Cyberpunk en Steam” por el hecho de que exista una fila GOG owned.

Este caso debe quedar como fixture de integración o smoke manual sanitizado, sin depender de credenciales ni de llamadas externas en la prueba.

## 15. Fases de implementación y criterios de aceptación

### Fase 0 — contrato y datos de prueba

- Congelar vocabulario de estados, tipos, namespaces y razones.
- Crear fixtures sanitizados para Cyberpunk 115/2940/316, DLC, ediciones, colisiones y appids conflictivos.
- Documentar restricciones reales de la base aplicada.

**Aceptación:** los fixtures expresan estados/tiendas distintos y no requieren cambiar el esquema.

### Fase 1 — servicio y endpoint de candidatos

- Extraer o extender servicio de reconciliación con consulta por lotes.
- Implementar confianza, razones, advertencias y bloqueos.
- Añadir DTOs, controller, autorización y contrato BFF.

**Aceptación:** endpoint read-only devuelve candidatos cross-state; una coincidencia de título nunca produce escritura ni auto-merge; respuesta inválida se rechaza.

### Fase 2 — merge seguro con invariantes

- Auditar colisiones de `user_library`, ids externos, `steam_games`, ofertas, reviews y favorites antes de escribir.
- Mantener lock global y orden determinista de filas.
- Extender contadores/snapshot para deduplicaciones y rollback definido.

**Aceptación:** casos sin conflicto preservan filas; conflictos de identidad/appid bloquean sin escrituras; colisiones permitidas son deterministas, idempotentes y auditadas.

### Fase 3 — UI de revisión y multi-select

- Mostrar estados, tiendas, ids, confianza, razones y warnings en modo solo lectura.
- No añadir acción de merge hasta que la solicitud esté vinculada a un `candidateKey`/evidencia vigente y exista rollback operativo.
- La selección/multi-select y el endpoint de escritura quedan pendientes; no se simula una acción de UI.

**Aceptación:** un operador puede revisar y fusionar varios grupos sin perder la selección ni ocultar operaciones aplicadas.

### Fase 4 — lecturas y smoke de producción controlado

- Verificar `ownedStores`, wishlist, categorías, reviews, favorites, ofertas y precios después de merge.
- Ejecutar checks SQL de invariantes y smoke con backup previo.

**Aceptación:** estados permanecen literales, GOG owned aparece en `ownedStores`, Steam wished sigue visible y ningún secreto entra en logs/documentos.

## 16. Riesgos y mitigaciones

| Riesgo | Mitigación |
|---|---|
| Falso positivo por título | Título solo candidato; evidencia/razones; confirmación humana |
| Confundir edición, DLC o remaster | Tipo/año/fecha, warnings y bloqueos explícitos |
| Appids Steam distintos | Bloqueo antes de escribir; no elegir arbitrariamente |
| `23505` al repuntar biblioteca/ofertas/favoritos | Preflight de colisiones y reglas deterministas dentro de transacción |
| UI opera sobre candidatos obsoletos | `409`, locks, recarga posterior y lote detenido ante error |
| Rollback incompleto | Snapshot/detalle de filas destruidas y backup previo; no prometer undo hasta implementarlo |
| Ocultar ownership real por filtro de estado | Allowlist explícita de `wished`, `owned`, `subscription` en reconciliación; pruebas de `ownedStores` |
| Costo de consulta en bibliotecas grandes | Índices existentes, proyección por lote, límites/paginación y métricas sin datos sensibles |
| Cambios de esquema innecesarios | Reutilizar DDL actual; migrar solo tras demostrar carencia |

## 17. Decisión final propuesta

Construir reconciliación como **sugerencia read-only + confirmación humana + merge transaccional**, no como sincronización automática. La identidad exacta y los ids externos gobiernan; el título y `normalized_title` ayudan a encontrar, pero nunca prueban. La fusión solo repunta referencias y conserva literalmente estados y tiendas, con reglas explícitas para colisiones y appids Steam conflictivos.
