# Deals Steam MX: índice de planes

Planes del proyecto. Cada uno es autónomo y se lanza desde el directorio del repo.

| Plan | Estado | Contenido |
|---|---|---|
| [PLAN_BASE_MVP.md](../docs/PLAN_BASE_MVP.md) | Aprobado | Arquitectura del producto y fases del MVP |
| [PLAN_ITAD.md](../docs/PLAN_ITAD.md) | Fases 1–4 completadas | Integación con IsThereAnyDeal (precios oficiales/autorizados) y servicio de tipo de cambio USD→MXN |
| [PLAN_GGDEALS.md](../docs/PLAN_GGDEALS.md) | Implementado (sin commitear) | Integación con gg.deals (precios oficiales y de keyshop) |
| [PLAN_BUNDLES.md](../docs/PLAN_BUNDLES.md) | Implementado V1 (solo visualización, sin commitear) | Bundles externos de ITAD en el detalle del juego; nunca entran en la comparación de precios |
| [PLAN_WISHLIST.md](../docs/PLAN_WISHLIST.md) | Parcial (sin commitear) | Wishlist de Steam y alertas por Telegram |
| [PLAN_CATALOG.md](../docs/PLAN_CATALOG.md) | Propuesto | Catálogo canónico de juegos: identidad compartida (`games` + `game_external_ids`) para wishlist, biblioteca y reseñas; deja preparado HowLongToBeat/IGDB |
| [PLAN_LIBRARY.md](../docs/PLAN_LIBRARY.md) | Propuesto | Biblioteca de juegos comprados: import de Playnite, binding con precios, tag de Game Pass y reseñas por plataforma |
| [PLAN_TELEGRAM.md](../docs/PLAN_TELEGRAM.md) | Propuesto | Notificaciones genéricas por Telegram (dispatcher de eventos) |
| [PLAYNITE_EXPORT.md](../docs/PLAYNITE_EXPORT.md) | Ejecutado | Export real de Playnite 10; contrato de import verificado con 2586 juegos |

## Resumen de proveedores

| Proovedor | Para qué | API key | Moneda | Webhooks |
|---|---|---|---|---|
| Steam Store API | Precio regional y detalles | No | MXN directo | No |
| IsThereAnyDeal | Precios de tiendas oficiales/autorizadas | Sí (header) | USD → convertido | Solo `notification-waitlist` (no usable) |
| gg.deals | Precios oficiales y de keyshop | Sí (query) | USD → convertido | No |
| Banxico SIE / Frankfurter | Tipo de cambio USD→MXN | Sí (Banxico) / No | FIX MXN/USD | No |
| Telegram Bot API | Notificaciones | Sí (bot token) | — | No |

## Estado del código

| Funcionalidad | Estado |
|---|---|
| Steam Store API (búsqueda, detalle, sugerencias, mínimo histórico) | Completado |
| ITAD (precios oficiales/autorizados, gate de 7 días, refresh manual, degradación) | Completado (sin commitear) |
| FX (Banxico + Frankfurter, tasa diaria, conversión en UI) | Completado (sin commitear) |
| gg.deals (oficiales + keyshops) | Completado (sin commitear) |
| Bundles externos (solo visualización) | Completado (sin commitear) |
| Wishlist de Steam (sync, BFF, `/wishlist`) | Completado (sin commitear) |
| Catálogo canónico de juegos (`games` + `game_external_ids` + `game_id`) | No implementado — `PLAN_CATALOG.md` |
| Biblioteca de juegos comprados (import de Playnite + binding con precios) | No implementado |
| Game Pass en biblioteca (`state='subscription'`, sin precio ni alerta) | No implementado — `PLAN_LIBRARY.md` §5 |
| Reseñas por plataforma (score 0–100, GOTY, meses inicio/fin, texto) | No implementado — `PLAN_LIBRARY.md` §9 |
| Alertas y Telegram | No implementado |
| Identidad cross-store multi-tienda (`LookupByShopAsync`) | No implementado — diferido, `PLAN_LIBRARY.md` §10 |
| Duración de juegos (HowLongToBeat / IGDB) | No implementado — diferido, `PLAN_CATALOG.md` §6 |