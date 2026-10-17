# Bundles multifuente y promociones con ntfy

Estado: propuesta; no implementa integraciones ni envíos. Continúa [PLAN_BUNDLES.md](PLAN_BUNDLES.md) y complementa [PLAN_TELEGRAM.md](PLAN_TELEGRAM.md) con un transporte ntfy opcional.

## Resultado recomendado

1. Consolidar ITAD y su cobertura de Fanatical antes de integrar otra fuente.
2. Validar una muestra de gg.deals bundles antes de escribir su parser.
3. Mantener Steam directo bloqueado hasta verificar un contrato permitido de descubrimiento y precios de bundles.
4. Capturar fechas de promociones cuando existan; usar un dispatcher local para ntfy, sin depender de programar mensajes con mucha anticipación.

No se mezclan bundles con `game_offers`, mínimos de juegos individuales ni ahorro de wishlist. No se incorpora scraping como alternativa a una API ausente.

## 1. Evidencia y límites

| Fuente | Evidencia disponible | Qué falta para implementar |
|---|---|---|
| ITAD ofertas | La documentación de `games/prices/v3` incluye `timestamp` y `expiry` nullable. El modelo `ItadDeal` actual no conserva expiry. | Fixture sanitizada con expiry presente y otra con null; confirmar significado y propagación hasta API/UI. `timestamp` no prueba inicio de promoción. |
| ITAD bundles | `games/overview/v2` incluye `publish`, `expiry`, tienda y tiers. Ya existe implementación y columnas `published_at`/`expires_at`. | Certificar fixtures MX y resolver discrepancias documentales antes de dar el contrato por cerrado. |
| Fanatical vía ITAD | ITAD incluye Fanatical como tienda de bundles. El plan existente registra un Build Your Own de Fanatical con precio de tier null. | Fixture de bundle fijo y de selección dinámica; no inferir precio total de un tier sin precio. |
| gg.deals | El plan existente documenta `/v1/bundles/by-steam-app-id/`, lotes de hasta 100 y región US, sin MX. | Respuesta real con key para contenido, precios, fechas, identidad y Build Your Own. La revisión web no pudo acceder a `/api/` (403); no confirma ni refuta el contrato del plan. |
| Steam directo | La documentación revisada de IStoreService no estableció un contrato de bundles por juego con precio regional. | Verificar método, condiciones de uso, identidad, contenido y precio MX. Una página pública no demuestra una API consumible. |
| Fanatical directo | Su FAQ oficial describe afiliación con CJ y Tapfiliate. No documenta allí API pública ni feed. | Consultar acceso autorizado a feed/API y sus condiciones. La ausencia en esa FAQ no demuestra que no exista un feed privado. |
| ntfy | Publicación diferida mediante Delay/At/In; límite predeterminado documentado de 10 segundos a 3 días, configurable en servidor propio. | Elegir servidor, autenticación, destino, anticipación y política de avisos antes de activar envíos. |

### Discrepancias del plan anterior

`PLAN_BUNDLES.md` declara al inicio que falta una respuesta real MX, pero §B.1 relata una confirmación en vivo. También combina estado V1.1 implementado con secciones de esquema propuesto. No se elimina ninguna evidencia: reconciliar con fixtures versionadas y el código antes de marcar fases completas. Este documento no certifica precios ni parser en producción.

## 2. Contrato de fechas para promociones

Propuesta de metadatos opcionales: `startsAt`, `endsAt`, `observedAt` y fuente. Persistencia, DTO y migración se diseñarán al implementar, sin convertir fechas desconocidas en fechas calculadas.

- Normalizar offsets a UTC. Guardar ausencia como null.
- Distinguir publicación del bundle, inicio de promoción, expiración y observación; no intercambiarlos.
- No asumir que el próximo refresco es el final de una promoción.
- Oferta sin fecha final: puede avisarse por precio válido, pero nunca como «termina pronto».
- Fecha inválida, pasada, contradictoria o snapshot stale: no programar aviso de vencimiento.
- Fecha o precio modificados: invalidar el aviso pendiente anterior y reevaluar con el snapshot vigente.
- Por fuente, aceptar fechas solo tras fixture y prueba. No se afirma hoy que gg.deals, Steam o Epic entreguen un fin utilizable por los clientes existentes.

## 3. Diseño propuesto de notificaciones ntfy

Reutilizar la separación productor → evento persistido → dispatcher del plan Telegram; ntfy es otro transporte, no otro scraper ni scheduler de precios. No se ejecuta aún ese plan ni se adopta su deduplicación sin revisión.

1. El productor recibe un snapshot válido y genera un evento elegible.
2. La clave idempotente incluye destinatario, juego o bundle, fuente, identidad de oferta, tipo de aviso y versión relevante de precio/expiración. No usar solo tienda, precio y día: colisionaría entre juegos.
3. Un dispatcher local selecciona eventos vencidos para entrega, revalida disponibilidad/fecha y publica por HTTPS a ntfy.
4. Registrar intentos, próximo intento, resultado y cancelación. Acotar reintentos y respetar rate limits. Un timeout después de publicar puede duplicar un envío: no prometer exactly-once.
5. No incluir tokens, topics privados ni URLs autenticadas en logs. Configuración deshabilitada por defecto; secretos solo backend.

Preferir programación local: permite cancelar o sustituir avisos cuando una tienda cambia la promoción y evita depender del máximo de demora del servidor ntfy. El envío diferido de ntfy queda como opción de corto plazo, no como almacenamiento de planificación.

Decisiones pendientes antes de implementar: servidor propio o servicio público; destino único o por usuario; avisos de precio objetivo, promociones gratuitas verificadas o próximo vencimiento; anticipación y frecuencia máxima. No se activan envíos sin resolverlas.

## 4. Plan de bundles por fases

| Fase | Trabajo | Criterio de cierre |
|---|---|---|
| A — ITAD/Fanatical | Reconciliar implementación/documentos; fixtures MX de bundle fijo, sin precio, expirado y Build Your Own. Mantener enlace y atribución ITAD. | Contenido/precio/moneda/fecha corresponden al mismo tier; null no aparece como gratis. Fanatical visible cuando ITAD lo devuelve. |
| B — gg.deals | Capturar muestra autorizada, validar fechas e identidad estable o clave derivada documentada; parser y governor compartido, purga aislada por fuente. | Pruebas de respuesta vacía, fallo, contenido incompleto, precio string y selección dinámica; US nunca etiquetado como precio regional MX. |
| C — Steam | Investigar contrato permitido antes de implementar cliente. Separar paquetes y bundles; contemplar precios personalizados por propiedad y disponibilidad regional. | Fuente y muestra MX verificadas. Sin contrato, fase detenida; enlace informativo no sustituye precio comparable. |
| D — Fanatical directo opcional | Solo si ITAD resulta insuficiente: solicitar feed/API autorizado, evaluar cobertura y condiciones. | Acceso concedido y contrato verificable; no duplicar bundles ni reescribir enlaces de ITAD. |
| E — Fechas y ntfy | Propagar expiry ITAD validado; modelo de eventos, dispatcher y transporte deshabilitado por defecto. | Pruebas con reloj controlado, deduplicación, cancelación, fallos y secretos; smoke contra destino de prueba explícitamente autorizado. |

Cada adaptador conserva identidad de origen. Una eventual agrupación visual entre fuentes necesita correspondencia demostrable de tienda, enlace e identidad; nunca fusionar por título solamente. Un fallo preserva el snapshot y lo marca stale, no equivale a «sin bundles».

## 5. Verificación prevista

- Pruebas deterministas con fixtures sanitizadas: códigos, contenido, fechas, moneda, null y límites. No guardar keys ni payloads personales.
- Ningún precio desconocido se convierte en cero; un bundle sin precio sigue siendo informativo.
- Build Your Own: no sumar todas las opciones como si fueran contenido comprado ni calcular ahorro fijo.
- Probar que purga y fallos de una fuente no afectan otra ni el comparador de juegos.
- Fechas: offset, null, vencimiento exacto, cambio de expiración y snapshot stale.
- ntfy con servidor simulado: éxito, 429, timeout ambiguo, reintento, cancelación y ausencia de filtración de secretos. No realizar envíos reales en pruebas automáticas.
- Al implementar: `dotnet build Deals.sln`, checks afectados y `pnpm build` en `Deals.Web`; este documento por sí solo no requiere builds.

## 6. Fuentes y siguiente paso

- ITAD: https://docs.isthereanydeal.com/ — precios y overview/bundles.
- ntfy: https://docs.ntfy.sh/publish/ — publicación diferida y límites.
- Fanatical: https://support.fanatical.com/hc/en-us/articles/360001415777-Affiliate-Partner-FAQ
- Steam: https://partner.steamgames.com/doc/webapi/IStoreService
- gg.deals: https://gg.deals/api/ — acceso web no disponible durante esta revisión; contrastar con muestra autorizada y [PLAN_GGDEALS.md](PLAN_GGDEALS.md).

Siguiente paso de bundles: fase A. Siguiente paso del falso «Gratis» de Epic: obtener un juego afectado, contrastar precio persistido y timestamp de refresco y reproducir el payload desde la imagen runtime autorizada. El parser ya rechaza 0/0; no eliminar indiscriminadamente 100% de descuento, porque también puede ser una promoción real.
