# DealExt: plan del proyecto base y MVP

Estado: aprobado.

Este documento es la referencia para agentes y personas que trabajen en el repositorio. Leer también `AGENTS.md` antes de modificar código.

## 1. Producto

DealExt compara precios de juegos de PC para México.

El MVP debe permitir:

1. Identificar un juego mediante su Steam App ID.
2. Consultar el precio oficial regional de Steam en MXN.
3. Consultar ofertas de tiendas oficiales o autorizadas mediante ITAD.
4. Consultar bundles externos mediante ITAD.
5. Consultar el precio agregado más bajo de keyshops mediante la API de gg.deals.
6. Convertir precios no-MXN a una estimación en MXN mediante un proveedor FX.
7. Mostrar claramente el origen, moneda y tipo de cada precio.

La comparación debe diferenciar:

- precio regional real;
- precio de tienda oficial o autorizada;
- estimación convertida a MXN;
- precio agregado de keyshops;
- bundle externo.

## 2. Base actual

La base ya renombrada contiene:

- `Deals.API`: API ASP.NET Core sobre .NET 9.
- `Deals.BusinessLogic`: servicios, repositorio genérico y `AppDbContext`.
- `Deals.Models`: entidades y modelos comunes.
- `Deals.Web`: frontend Next.js 15 App Router que funciona como BFF.
- PostgreSQL mediante Npgsql EF Core.
- JWT, refresh tokens, sesiones, CSRF, rate limiting y manejo de errores.
- El vertical slice de Users como referencia.

Hechos no negociables:

- Mantener `net9.0`. No subir a .NET 10.
- `Deals.sln` contiene solamente los tres proyectos backend.
- `Deals.Web` es un proyecto pnpm separado.
- `SQL/schema.sql` es la fuente de verdad del esquema.
- Las modificaciones para bases existentes son migraciones SQL fechadas.
- No existe todavía un proyecto de pruebas automatizadas.
- El dominio viejo de gastos no debe reintroducirse.

## 3. Nombres

- Nombre técnico: `Deals`.
- Marca visible: `DealExt`.
- Proyectos: `Deals.API`, `Deals.BusinessLogic`, `Deals.Models`.
- Frontend: `Deals.Web`.
- Contenedores: `dealext-api`, `dealext-frontend`.

No usar `App`, `AppStarter`, `Gastos`, `expense` ni nombres del proyecto anterior en código nuevo.

## 4. Arquitectura aprobada

Usar un monolito modular:

```text
Navegador
  -> Next.js BFF
  -> Deals.API
  -> servicios de aplicación
  -> adaptadores Steam / ITAD / gg.deals / FX
  -> PostgreSQL y caché
```

No crear microservicios, colas, event bus ni una aplicación separada por proveedor.

El navegador no debe llamar directamente a los proveedores externos. Las claves de API deben permanecer en el backend. El frontend consume el BFF existente y el BFF consume la API .NET.

Los controladores deben coordinar el límite HTTP. La lógica de proveedores, normalización, clasificación, conversión y combinación pertenece al backend.

## 5. Responsabilidad de cada fuente

### Steam

Fuente de verdad para el precio oficial regional de Steam México.

Debe consultar el storefront con la región `mx` y conservar la moneda devuelta por Steam. Cuando el resultado sea MXN regional:

```text
classification: official
pricingType: regional
source: steam
```

Steam es la fuente principal para el precio de Steam. El MVP no implementa login de Steam ni cálculo personalizado de bundles según juegos poseídos.

### ITAD

IsThereAnyDeal proporciona:

- ofertas de tiendas oficiales o autorizadas;
- precio regular y descuento cuando estén disponibles;
- mínimos históricos cuando estén disponibles;
- bundles externos de tiendas como Humble o Fanatical;
- identificación externa del juego.

ITAD no se debe usar para fingir que una oferta es precio regional MXN si la API devolvió otra moneda.

```text
classification: official | authorized
source: itad
pricingType: regional | fx_estimate
```

La clasificación de tiendas debe controlarse con una tabla o configuración local de tiendas permitidas. No asumir que el proveedor entrega una clasificación perfecta.

### gg.deals

La API de gg.deals se usa para el precio agregado mínimo de keyshops.

La API gratuita no debe interpretarse como una lista de vendedores individuales. Si no entrega el nombre del vendedor, no inventarlo.

```text
classification: keyshop
source: ggdeals
storeId: null
storeName: "GG.deals keyshops"
pricingType: fx_estimate
```

No hacer scraping de gg.deals, no parsear HTML y no usar Playwright para completar datos faltantes.

### FX

FX convierte una cotización no-MXN en una estimación para comparación:

```text
MXN estimado = importe original × tipo de cambio
```

Toda conversión debe conservar:

- moneda original;
- importe original;
- tipo de cambio utilizado;
- fuente del tipo de cambio;
- momento de consulta;
- importe MXN estimado.

## 6. Precio regional frente a estimación FX

Esta distinción es parte del contrato del producto.

### Precio regional

Precio entregado por la tienda para México y en MXN. Es el precio real de esa región para esa fuente.

```text
isRegional: true
isEstimate: false
```

### Estimación FX

Precio recibido en USD, EUR u otra moneda y convertido a MXN. No significa que la tienda vaya a cobrar ese importe exacto en pesos.

```text
isRegional: false
isEstimate: true
```

La UI debe mostrar etiquetas distintas. Nunca presentar una conversión USD->MXN como precio regional real.

Si una tienda devuelve MXN directamente, no pasar ese valor por FX.

## 7. Identidad del juego

El identificador principal del MVP es:

```text
steamAppId
```

Los identificadores externos se guardan como metadatos secundarios:

```text
steamAppId <-> itadGameId
steamAppId <-> ggDeals identifier
```

No usar búsqueda difusa por título como mecanismo principal. Una edición Deluxe, DLC, demo o producto diferente puede tener otro App ID.

Distinguir cuando corresponda:

- Steam App ID;
- Steam package/sub ID;
- Steam bundle ID;
- identificador de juego en ITAD;
- identificador de gg.deals.

Si un proveedor no encuentra el App ID, devolver estado parcial para ese proveedor. No sustituir silenciosamente por otra edición.

## 8. Modelo normalizado

Los DTOs del backend deben mapear las respuestas externas a un modelo interno. No exponer payloads crudos de Steam, ITAD, gg.deals o FX.

Modelo conceptual de oferta:

```text
Offer
- steamAppId
- source: steam | itad | ggdeals
- classification: official | authorized | keyshop
- storeId: nullable
- storeName
- originalAmountMinor
- originalCurrency
- mxnAmountMinor: nullable
- pricingType: regional | fx_estimate
- discountPercent: nullable
- productUrl
- observedAtUtc
- expiresAtUtc: nullable
```

Reglas monetarias:

- Guardar dinero en unidades menores enteras, no en `double`.
- Toda cantidad debe incluir moneda.
- Todos los timestamps deben ser UTC.
- Una oferta sin fecha de observación no debe considerarse válida.
- No comparar ofertas con distinta confianza sin mostrar la categoría y el método.

Modelo conceptual de bundle externo:

```text
ExternalBundle
- source: itad
- externalId
- title
- storeName
- originalAmountMinor
- originalCurrency
- mxnAmountMinor: nullable
- pricingType
- includedItems
- productUrl
- observedAtUtc
- expiresAtUtc: nullable
```

En V1 se muestra el bundle. El ahorro solo se calcula si el proveedor entrega contenido y precios comparables de forma inequívoca.

## 9. Flujo de consulta

La ruta principal será equivalente a:

```http
GET /v1/games/{steamAppId}/offers
```

Flujo del backend:

1. Validar que el App ID sea un entero positivo dentro del rango permitido.
2. Consultar Steam, ITAD y gg.deals en paralelo cuando sea posible.
3. Resolver el mapping de ITAD si es necesario.
4. Consultar FX solamente cuando haya ofertas que necesiten conversión.
5. Normalizar las respuestas.
6. Clasificar las tiendas.
7. Convertir importes no-MXN a estimaciones MXN.
8. Separar ofertas duplicadas por fuente, tienda y producto.
9. Calcular los mejores resultados por categoría.
10. Devolver estados independientes por fuente.

Un fallo de ITAD no debe ocultar un precio válido de Steam. Un fallo de FX no debe ocultar precios regionales.

La respuesta debe separar como mínimo:

```text
bestOfficial
bestAuthorized
bestKeyshop
externalBundles
sourceStatuses
```

Si existe `bestOverall`, debe incluir siempre `classification` y `pricingType`.

## 10. Caching y límites

Implementar caché en el backend, nunca en el cliente para ocultar errores de frescura.

Namespaces sugeridos:

```text
dealext:steam:offers:{steamAppId}
dealext:itad:offers:{steamAppId}
dealext:itad:bundles:{steamAppId}
dealext:ggdeals:keyshop:{steamAppId}
dealext:fx:{base}:{quote}
```

TTL inicial orientativo:

```text
Steam:    1 hora
ITAD:     30 minutos
GGDeals:  30-60 minutos
FX:       30 minutos
```

Respetar siempre los límites documentados de cada proveedor. No generar llamadas por cada render del frontend.

Para una sola instancia se puede comenzar con `IMemoryCache`. Redis se incorpora cuando exista una necesidad real de compartir caché entre instancias o procesos.

Una respuesta cacheada debe guardar al menos:

- datos normalizados;
- `observedAtUtc`;
- `expiresAtUtc`;
- fuente;
- estado fresco o stale.

Una caché vencida puede servir como fallback etiquetado `stale` cuando el proveedor no responde. No ocultar el estado.

## 11. Persistencia mínima

Conservar la infraestructura actual de usuarios, sesiones y FX.

Agregar tablas solo cuando el flujo las necesite. Candidatos:

- `games`;
- `stores`;
- `game_source_mappings`;
- `price_quotes`;
- `external_bundles`;
- `fx_rates`.

No crear tablas de alertas, compras, recomendaciones o actividad de usuario en el MVP.

Toda modificación del esquema debe actualizar `SQL/schema.sql` y agregar una migración SQL fechada para bases existentes.

## 12. Frontend

El frontend conserva el patrón Next.js BFF:

```text
Deals.Web/app/api/bff/*
Deals.Web/lib/bff/*
Deals.Web/lib/contracts/*
Deals.Web/middleware.ts
```

Páginas iniciales:

```text
/buscar
/games/{steamAppId}
```

La página de juego debe mostrar por separado:

- mejor tienda oficial;
- mejor tienda autorizada;
- mejor keyshop agregado;
- bundles externos;
- moneda original;
- equivalente MXN;
- tipo de precio;
- fecha de actualización;
- estado de cada fuente.

Usar `csrfFetch`, `parseApiError`, `fetchApiWithAutoRefresh` y las utilidades de sesión existentes. No crear un segundo flujo de autenticación.

Una nueva ruta privada debe añadirse a `privateRoutes` y al matcher de `Deals.Web/middleware.ts`.

## 13. Fases de implementación

### Fase 0: Base

- Confirmar rename `Deals`/`DealExt`.
- Mantener auth, BFF, CSRF, PostgreSQL y Users como infraestructura reutilizable.
- Confirmar `dotnet build Deals.sln` y `pnpm build`.

### Fase 1: Contratos

- Crear modelos de oferta, dinero, fuente, clasificación, bundle y estado de proveedor.
- Definir DTOs de API y contratos TypeScript.
- Definir la respuesta parcial por fuente.
- No llamar todavía a APIs externas.

### Fase 2: Steam

- Implementar adapter de Steam.
- Consultar precio MXN regional.
- Probar disponibilidad, juego gratuito, sin precio y App ID inválido.
- Exponer un primer flujo de juego completo.

### Fase 3: FX

- Implementar conversión de cotizaciones no-MXN.
- Guardar timestamp y fuente del tipo de cambio.
- Etiquetar todas las conversiones como `fx_estimate`.

### Fase 4: ITAD

- Resolver `steamAppId` a identificador ITAD.
- Consultar ofertas actuales e históricos disponibles.
- Clasificar tiendas mediante configuración local.
- Consultar y mostrar bundles externos.

### Fase 5: gg.deals

- Consultar el agregado de keyshops por Steam App ID.
- Convertirlo con FX si no está en MXN.
- Mostrarlo separado de oficiales y autorizadas.
- Respetar atribución y enlaces de la API.

### Fase 6: BFF y UI

- Crear contrato TypeScript.
- Crear rutas BFF.
- Crear búsqueda y detalle.
- Añadir estados de loading, vacío, parcial y error.
- Añadir navegación y protección de rutas si aplica.

### Fase 7: Hardening

- Ajustar TTLs y rate limits.
- Añadir cache stampede protection si es necesario.
- Medir latencia por fuente.
- Confirmar fallos parciales y fallback stale.
- Ejecutar smoke test completo.

## 14. Verificación

Después de cada unidad lógica:

```bash
dotnet build Deals.sln
```

```bash
cd Deals.Web
pnpm build
```

Para cambios de frontend o backend, ejecutar también los diagnósticos disponibles.

Antes de considerar terminado el MVP:

1. Registrar o iniciar sesión con el flujo existente.
2. Consultar un juego válido por Steam App ID.
3. Confirmar precio regional Steam en MXN.
4. Confirmar una oferta ITAD o estado parcial correcto.
5. Confirmar bundle externo cuando exista.
6. Confirmar precio agregado de gg.deals cuando exista.
7. Confirmar que USD convertido aparece como estimación.
8. Confirmar que un fallo de una fuente no elimina las demás ofertas.
9. Confirmar que no se ejecuta scraping.
10. Confirmar que no hay claves de proveedor en el frontend.

## 15. Reglas para agentes

- Leer `AGENTS.md` y este documento antes de trabajar.
- No implementar proveedores antes de definir su contrato normalizado.
- No usar scraping, HTML parsing ni Playwright para datos de precios.
- No poner claves externas en Next.js ni en código cliente.
- No reescribir auth, BFF, CSRF o refresh tokens sin una razón explícita.
- No introducir .NET 10, EF migrations tooling, microservicios ni CQRS innecesario.
- No añadir dependencias si el stack actual resuelve la necesidad.
- No usar `double` para dinero.
- No ocultar errores de proveedores. Devolver estado parcial.
- No presentar una estimación FX como precio regional.
- No inventar nombres de keyshops cuando gg.deals solo entregue un agregado.
- No borrar Users/auth porque todavía no formen parte de la primera pantalla.
- No mezclar cambios de infraestructura con cambios de dominio sin necesidad.
- No afirmar que una integración funciona hasta ejecutar una prueba real o una verificación documentada.
- Si el código contradice una decisión de este documento, detenerse y corregir el plan o pedir una decisión explícita.
