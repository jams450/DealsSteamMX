# Deals Steam MX: índice de planes

Planes del proyecto. Cada uno es autónomo y se lanza desde el directorio del repo.

| Plan | Estado | Contenido |
|---|---|---|
| [PLAN_BASE_MVP.md](PLAN_BASE_MVP.md) | Aprobado | Arquitectura del producto y fases del MVP |
| [PLAN_ITAD.md](PLAN_ITAD.md) | Fases 1–4 completadas | Integación con IsThereAnyDeal (precios oficiales/autorizados) y servicio de tipo de cambio USD→MXN |
| [PLAN_GGDEALS.md](PLAN_GGDEALS.md) | Propuesto | Integación con gg.deals (precios oficiales y de keyshop) |
| [PLAN_WISHLIST.md](PLAN_WISHLIST.md) | Propuesto | Wishlist de Steam, biblioteca poseída por Playnite, alertas y Telegram |

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
| gg.deals | No implementado |
| Biblioteca poseída (Playnite) | No implementado |
| Wishlist de Steam | No implementado |
| Alertas y Telegram | No implementado |