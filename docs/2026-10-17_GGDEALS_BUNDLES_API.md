# GG.deals Bundles API — guía para agentes

Última actualización: 2026-10-17.
Fuente: https://gg.deals/api/bundles/
Procedencia: documentación aportada por el usuario tras leer la página completa en navegador el 2026-10-17. Último cambio listado: 2026-10-14 (`by-slug`). Esta copia conserva el contrato y las advertencias de esa guía; no constituye validación mediante API key ni respuesta real.

## Alcance

Consulta bundles por identificador y lista bundles actualmente activos. No es la API de precios individuales ni ofrece búsqueda por título. Las consultas por identificador pueden devolver resultados históricos o caducados.

## Transporte y autenticación

- GET para todos los endpoints.
- Base: `https://api.gg.deals/v1/bundles/`.
- Query `key` obligatoria. No Bearer.
- Obtener la clave mediante configuración privada; no solicitarla por chat ni incluirla en documentos, historial, logs o respuestas.
- Redactar `key` también en URLs de paginación y errores. No registrar URLs completas.

## Endpoints

| Entrada | Ruta | Parámetro |
|---|---|---|
| Steam `/app/<id>/` | `by-steam-app-id/` | `ids` |
| Steam `/sub/<id>/` | `by-steam-sub-id/` | `ids` |
| Steam `/bundle/<id>/` | `by-steam-bundle-id/` | `ids` |
| GG.deals `/game/<slug>/` | `by-slug/` | `slugs` |
| Catálogo activo | `active/` | Ninguno de selección |

`ids` y `slugs`: separados por comas, máximo 100 por solicitud. No hay paginación documentada en las cuatro rutas `by-*`. No confundir App ID, Sub ID y Bundle ID. Si solo se conoce el título, resolver identidad antes; nunca adivinar IDs o slugs.

Plantillas, no solicitudes ejecutadas:

```text
GET https://api.gg.deals/v1/bundles/by-steam-app-id/?ids=420,252410&key=<API_KEY>&region=us
GET https://api.gg.deals/v1/bundles/by-steam-sub-id/?ids=7,1257696&key=<API_KEY>&region=us
GET https://api.gg.deals/v1/bundles/by-steam-bundle-id/?ids=232,13014&key=<API_KEY>&region=us
GET https://api.gg.deals/v1/bundles/by-slug/?slugs=half-life-2-episode-two,steamworld-dig&key=<API_KEY>&region=us
GET https://api.gg.deals/v1/bundles/active/?key=<API_KEY>&region=us
```

## Regiones y monedas

`region` opcional, predeterminada `us`. Admitidas:

`au`, `be`, `br`, `ca`, `ch`, `de`, `dk`, `es`, `eu`, `fi`, `fr`, `gb`, `ie`, `it`, `nl`, `no`, `pl`, `se`, `us`.

**No existe `mx` en esta lista.** No enviarlo ni sustituirlo silenciosamente. Acordar y mostrar una región de referencia: no acredita disponibilidad ni precio final para México. Región no equivale a moneda; leer `currency` de cada tier. No comparar monedas distintas ni inventar conversiones.

## Sobre por identificador

Éxito: `success: true`, `data` objeto indexado por los identificadores solicitados como claves de texto (o slugs). Valores: `GameBundles` o null.

- `data[id] == null`: GG.deals no encuentra el juego con ese identificador; no significa que no exista.
- `bundles: []`: juego identificado sin bundles devueltos.
- `bundles` con elementos: asociaciones posiblemente históricas.
- Clave ausente: anomalía; no asumir equivalencia con null.

### GameBundles

| Campo | Tipo | Significado |
|---|---|---|
| `title` | string | Título del juego o paquete |
| `url` | string | Página del juego o paquete en GG.deals |
| `bundles` | Bundle[] | Bundles asociados |

### Bundle

| Campo | Tipo | Significado |
|---|---|---|
| `title` | string | Nombre |
| `url` | string o null | Artículo GG.deals; no necesariamente enlace de compra; null si no tuvo artículo |
| `dateFrom` | string UTC | Inicio de disponibilidad |
| `dateTo` | string UTC o null | Fin; null significa desconocido |
| `tiers` | BundleTier[] | Tiers con precio y contenido |

Los ejemplos usan `YYYY-MM-DD HH:mm:ss` sin sufijo, pero la descripción indica UTC: interpretar explícitamente como UTC, nunca hora local. El formato se observa en ejemplos, no en un esquema formal.

### BundleTier

| Campo | Tipo | Significado |
|---|---|---|
| `price` | string | Precio del tier con dos decimales |
| `currency` | string | Código de tres letras |
| `gamesCount` | entero o null | Número de juegos elegibles para escoger |
| `games` | BundleTierGame[] | Juegos disponibles en el tier |

- `gamesCount == null`: todos los juegos listados están incluidos.
- `gamesCount == 2` con diez juegos: escoger dos de diez, no comprar diez.
- El precio corresponde al tier completo. No asumir tiers acumulativos.
- Comprobar que el juego pertenece al tier relevante antes de afirmar que puede obtenerse allí.
- Aritmética decimal para dinero. Precio dividido por juegos es promedio calculado, nunca precio individual de compra.

### BundleTierGame

Solo `title` y `url` (strings) documentados. Usar la URL como referencia de identidad cuando pueda corroborarse, no solo títulos. No inventar Steam IDs, DRM, vendedor, impuestos, stock, enlace directo de compra o historial de cambios.

## Catálogo activo y paginación

En `active/`, `success: true` y `data` contiene:

| Campo | Tipo | Significado |
|---|---|---|
| `previous` | URL o null | Página anterior |
| `next` | URL o null | Página siguiente |
| `totalCount` | entero | Total de bundles activos entre todas las páginas |
| `bundles` | Bundle[] | Página actual |

1. Consultar primera página con key y región.
2. Acumular bundles y conservar `totalCount` mediante código.
3. Seguir `next` hasta null, verificando antes HTTPS, host exacto `api.gg.deals` y ruta `/v1/bundles/active/`.
4. No enviar secretos a otro dominio. No fabricar ni modificar `cursor` u `offset`.
5. Detectar URLs repetidas para evitar bucles.
6. Cursores válidos durante una hora desde la solicitud inicial. Si caducan, enumeración incompleta: reiniciar el barrido, no certificar ni publicar parcial como completo.
7. Comparar total recolectado con `totalCount` antes de afirmar «todos»; reportar diferencias o cambios durante la enumeración.

El ejemplo oficial incluye `offset=100`, pero no documenta tamaño configurable ni garantiza página constante. No hay ID único de bundle documentado. `url` nullable impide deduplicación solo por URL; cualquier clave compuesta debe documentarse como heurística del cliente.

## Cuotas y errores

Por API key: **100 registros/minuto, 1000 registros/hora**.

- `by-*`: cada ID/slug consume un registro; agrupar 100 no reduce consumo.
- `active/`: cada solicitud consume un registro independientemente de bundles devueltos.
- Solicitudes inválidas también consumen cuota.
- HTTP429 si se excede cuota o el lote supera capacidad restante; por ejemplo 10 IDs con capacidad5 no devuelve datos.

Cabeceras documentadas: `x-ratelimit-limit`, `x-ratelimit-remaining`, `x-ratelimit-reset`. Aunque se describan como «requests», no interpretar capacidad restante como número de lotes100. Unidad/formato del reset y representación simultánea de ventanas no especificados aquí.

Error429 publicado:

```json
{
  "success": false,
  "data": {
    "name": "Too Many Requests",
    "message": "You can fetch bundles info only for 100 games per minute. Try again later.",
    "code": 429,
    "status": 429
  }
}
```

Recomendaciones del cliente, no garantías adicionales del proveedor:

- Validar/deduplicar entradas y coordinar consumidores de la misma key para ambas ventanas.
- Ante429, reducir lote si corresponde o esperar reinicio; nunca bucle inmediato.
- Validar HTTP, contenido JSON, `success` y estructura. HTML de bloqueo no es datos.
- Reintentos limitados y crecientes en errores transitorios; no reintentar indefinidamente permanentes.
- No inventar `Retry-After`, códigos de autenticación o textos de error no documentados.

## Presentación y vigencia

Preferir `active/` para ofertas activas o contrastar consultas históricas contra su enumeración completa. `dateTo` pasado no es activo; null no es perpetuidad ni disponibilidad garantizada. Guardar fuente, región, moneda e instante de consulta UTC.

Mostrar título, tier relevante, precio/moneda, reglas de selección, fechas y enlace disponible. Separar datos, cálculos e incertidumbres. No presentar ejemplos como ofertas actuales. GG.deals no garantiza exactitud o disponibilidad: verificar en tienda antes de comprar.

## Inconsistencia de ejemplos oficiales

El ejemplo `by-steam-bundle-id` solicita `ids=1,232,13014`, pero muestra claves `1`, `7`, `1257696` que corresponden parcialmente al ejemplo Sub IDs. No copiar esa correspondencia ni cambiar IDs para hacerla coincidir. Verificar respuestas indexadas por IDs solicitados al integrar.

## Prueba pendiente y límites de esta copia

No se ejecutaron llamadas autenticadas ni se comprobaron acceso de cuenta, respuestas reales, cuotas o paginación. No se acreditan precios de acceso, SLA, retención de caché, derechos de redistribución ni OpenAPI formal. Revisar condiciones antes de una integración comercial.

Esta evidencia corrige la auditoría previa: **sí hay endpoint global activo documentado en gg.deals**; HTTP403 del entorno de agentes no prueba ausencia de API. Su implementación no forma parte de la ingesta ITAD actual. No incorporar guía documental como fixture de una respuesta real.
