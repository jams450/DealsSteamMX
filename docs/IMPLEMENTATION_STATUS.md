# DealExt: implementation status

Este documento es el índice operativo del estado de implementación. Los documentos `PLAN_*.md` conservan el diseño, las decisiones, los contratos externos, las mediciones y el razonamiento técnico de cada dominio; no deben usarse como una lista global de tareas sin consultar primero esta página.

## Estado general

El comparador principal está implementado: precios directos de Steam, ofertas oficiales/autorizadas de ITAD, agregado de gg.deals, conversión FX a MXN y precios nativos de Epic y Microsoft. La biblioteca, wishlist, catálogo canónico, bundles V1 y varias integraciones de frontend también tienen implementación. Las verificaciones marcadas como pendientes deben cerrarse con evidencia antes de cambiar su estado a verificado.

## Mapa de implementación

| Área | Estado actual | Verificación o evidencia | Trabajo pendiente | Referencia |
|---|---|---|---|---|
| Base/MVP | Implementado | Comparador verificado en producción; commits documentados en el plan | Mantener como referencia del producto | [`PLAN_BASE_MVP.md`](PLAN_BASE_MVP.md) |
| ITAD | Implementado | Cliente, persistencia, FX, BFF y UI implementados y verificados | Fase de alertas/Telegram | [`PLAN_ITAD.md`](PLAN_ITAD.md) |
| gg.deals | Implementado | Fases 1–4; migración, ofertas, UI y builds documentados | Deuda técnica menor; bundles fuera de alcance | [`PLAN_GGDEALS.md`](PLAN_GGDEALS.md) |
| Multi-tienda | Parcialmente implementado | Fases 0–2 desplegadas y verificadas; Epic y Microsoft entregan MXN nativo | Fases 3–6, con alcance real revisado en el plan | [`PLAN_MULTISTORE.md`](PLAN_MULTISTORE.md) |
| Catálogo canónico | Parcialmente implementado | Fases 1–2: `games`, `game_external_ids`, backfill y resolución en inserciones | IGDB y HowLongToBeat permanecen como fuentes futuras | [`PLAN_CATALOG.md`](PLAN_CATALOG.md) |
| Biblioteca | Implementado | Import de Playnite, binding, `/library`, favoritos, carátulas y reseñas | Integraciones por tienda, Steam API y HowLongToBeat están diferidas | [`PLAN_LIBRARY.md`](PLAN_LIBRARY.md) |
| Biblioteca de consola | Parcialmente implementado | Fases 1–5 implementadas según el estado del plan | Verificación viva y fases restantes si aplican | [`PLAN_CONSOLE.md`](PLAN_CONSOLE.md) |
| Wishlist | Implementado | Sync de Steam, historial de mínimos, controller, BFF, contratos y UI | Alertas, Telegram, `price_alerts` y cuentas externas | [`PLAN_WISHLIST.md`](PLAN_WISHLIST.md) |
| Totales de wishlist | Implementado; smoke pendiente | Dos escenarios de subtotal calculados en servidor | Ejecutar y registrar verificación en vivo | [`PLAN_WISHLIST_PAQUETE.md`](PLAN_WISHLIST_PAQUETE.md) |
| Bundles externos | Implementado parcialmente | V1.1: descubrimiento ITAD, persistencia, tiers y UI | Fixture sanitizada de `overview/v2` y certificación del parser; gg.deals fuera de V1 | [`PLAN_BUNDLES.md`](PLAN_BUNDLES.md) |
| Telegram y alertas | Propuesto | No hay código de esta funcionalidad | Aprobación del alcance y diseño antes de implementar | [`PLAN_TELEGRAM.md`](PLAN_TELEGRAM.md) |

## Qué documento es fuente de verdad

- **Estado actual y prioridades:** este archivo.
- **Contrato y diseño de cada dominio:** el `PLAN_*.md` enlazado en la tabla.
- **Convenciones, arquitectura del repositorio y comandos de verificación:** [`AGENTS.md`](../AGENTS.md).
- **Estado del código:** el árbol de trabajo, commits y evidencia de verificación; un plan no sustituye una prueba ejecutada.

## Pendientes activos consolidados

1. Cerrar la verificación viva de los totales de paquete de wishlist.
2. Obtener una fixture sanitizada de bundles ITAD y certificar el parser sin guardar secretos ni datos innecesarios.
3. Revisar las fases pendientes de multi-tienda antes de implementarlas; el alcance estimado original ya no es completamente válido.
4. Decidir si se implementan alertas y Telegram. `PLAN_TELEGRAM.md` sigue siendo propuesta, no trabajo aprobado.
5. Mantener separadas las fuentes futuras del catálogo —IGDB y HowLongToBeat— de las capacidades ya implementadas.

## Convención para actualizar el estado

Cada cambio de estado debe incluir:

- el área afectada;
- la nueva clasificación: `Propuesto`, `Activo`, `Parcialmente implementado`, `Implementado`, `Verificado` o `Diferido`;
- la evidencia concreta: commit, build, migración aplicada, consulta de comprobación o smoke test;
- los pendientes que permanecen;
- el enlace al plan técnico correspondiente.

No se deben borrar los planes técnicos únicamente porque sus fases hayan terminado. Si un plan deja de ser útil como referencia, se mueve a `docs/archive/` y se conserva su historial; no se elimina sin una decisión explícita.

## Documentos históricos o superados

[`PLAN_IMPLEMENTACION_BIBLIOTECA.md`](PLAN_IMPLEMENTACION_BIBLIOTECA.md) fue un orden ejecutable inicial para biblioteca, catálogo y wishlist. Las implementaciones actuales están documentadas en los planes de cada dominio, por lo que este archivo debe tratarse como histórico y no como fuente de tareas nuevas.
