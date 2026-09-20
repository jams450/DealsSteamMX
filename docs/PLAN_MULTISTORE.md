# DealExt: plan multi-tienda (Epic / Xbox / Ubisoft)

Estado: **Fases 0 y 1 implementadas, desplegadas y verificadas en producción** (Epic devuelve MXN
nativo en la ficha). Fases 2 a 6 pendientes. Commit `865ec95`; migraciones `2026-10-02` y `2026-10-03`
aplicadas a mano en el servidor y `SQL/checks/game_offers_anchor.sql` en 0 filas.

Continúa a `PLAN_CATALOG.md` (identidad canónica), `PLAN_ITAD.md` (comparador actual),
`PLAN_GGDEALS.md` (keyshops) y `PLAN_LIBRARY.md` §10 (`LookupByShopAsync`). Leer también `AGENTS.md`.

## 0. Veredicto

| Pregunta | Respuesta verificada |
|---|---|
| ¿Sirve `steam → igdb → otras tiendas`? | **No hace falta IGDB.** ITAD ya hace el mapeo cross-store gratis y **sin API key**, en las dos direcciones, para Steam/Epic/GOG/Microsoft. IGDB añadiría OAuth, 4 req/s y licencia no comercial para entregar un subconjunto de lo que ya tenemos. |
| ¿Hay que crear una "BD de juegos con fuente matriz"? | **Ya existe.** `games` + `game_external_ids` de `PLAN_CATALOG.md` es exactamente eso. El trabajo es **poblarla** para namespaces no-Steam, no sustituirla. |
| ¿Por qué el precio de Epic MX "no sale"? | **Porque ITAD no tiene precio regional MX.** Devuelve el precio de **EE.UU. en USD**, idéntico para Steam, Epic, Microsoft y Humble. Ver §2. |
| ¿Se puede tarificar Epic y Xbox en MXN nativo? | **Sí, sin key y sin scraping de HTML.** Epic vía GraphQL público de su storefront; Xbox vía la API de catálogo de Microsoft. Ambos devuelven MXN real de MX. §4. |
| ¿Y Ubisoft? | **Precio directo bloqueado.** `store.ubisoft.com` está tras Akamai y no expone API pública. **No hace falta scraper ni IGDB:** el catálogo Ubisoft se vende en Epic y en Microsoft, que sí dan precio MX, y estos devuelven el publisher en la misma respuesta. La tienda propia se cubre con un enlace de revisión manual. §4.3. |
| ¿Hay que buscar por nombre el juego en cada tienda? | **No.** Hay una cadena **exacta** sin coincidencia por título: appid de Steam → UUID de ITAD (sin key) → `deal_url` que ITAD ya devuelve → seguir 1 redirección → id exacto de la tienda. §4.5. |
| ¿Es la única tienda bloqueada? | **No.** Ubisoft lo está; también Amazon (195 juegos en la biblioteca), Humble (39) y Battle.net (11). GOG no está bloqueada, pero **se descarta por redundante**: factura México en USD y su precio lo da ya ITAD shop 35. §4.4. |

## 1. Cómo se midió (todo solo lectura, sin secretos)

| Sonda | Resultado |
|---|---|
`POST https://api.isthereanydeal.com/lookup/id/shop/{id}/v1` | **HTTP 200 sin API key.** Mapea id de tienda → UUID de ITAD. |
`https://store.epicgames.com/graphql` | **HTTP 200 sin key.** `searchStore(keywords, country:"MX")`, `catalogOffer(namespace, id)`, `StorePageMapping(pageSlug)`.
`https://itad.link/<id>/?app=…` (el `deal_url` que ya persistimos) | **HTTP 200 con seguimiento.** Redirige a la URL de tienda con el id exacto dentro (§4.5). |
`https://apps.microsoft.com/api/products/search?query=…&hl=es-MX&gl=MX` | **HTTP 200 sin key.** Devuelve `productId`, `packageFamilyNames`, `price`, `displayPrice`, `strikethroughPrice`, `encodedTitle`, `redirectUrl`. |
`https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&value=…&market=MX` | **HTTP 200.** |
`https://store.steampowered.com/api/appdetails?appids=…&cc=mx` | **HTTP 200.** Control de contraste. |
`https://api.isthereanydeal.com/service/shops/v1?country=MX` | 200 sin key: **34 tiendas**, incluye 16 (Epic), 48 (Microsoft), 62 (Ubisoft), 61 (Steam). |
`https://api.isthereanydeal.com/games/prices/v3` sin key | **HTTP 403 `{"reason_phrase":"Missing api key"}`**. |
`https://store.ubisoft.com/**` | **HTTP 302** a página de challenge de Akamai; `/graphql` y `/api/search` igual. |

### Formatos de id externo, medidos uno por uno

| Namespace | Formato aceptado | Formato rechazado (→ `null`) |
|---|---|---|
| `steam` (shop 61) | `app/921570` | — |
| `epic` (shop 16) | offerId hex `f51ebf66eb42420bba6cdd7d87cf56af` **y** slug `octopath-traveler` | AppName de Playnite `Ovenbird`; sandboxId |
| `xbox` (shop 48) | StoreId **en minúsculas** `9nkx70bbcdrn` | StoreId en mayúsculas; PackageFamilyName; `LegacyXboxProductId` |
| `gog` (shop 35) | numérico `1443428641` (el id de Playnite) | — |
| `ubisoft` (shop 62) | **sin verificar** (el id de Playnite `16732` → `null`) | — |

La capitalización del StoreId de Microsoft es un caso real de fallo silencioso: `9NKX70BBCDRN` y
`9nkx70bbcdrn` son el mismo producto y solo el segundo resuelve.

## 2. Causa raíz: ITAD no tiene precio regional de MX

`POST games/prices/v3?country=MX` con el UUID de OCTOPATH TRAVELER
(`018d937f-07e9-7041-8d73-511c3bb2c94f`):

```
    48 Microsoft Store            USD  17.99  cut=70%
    16 Epic Game Store            USD  17.99  cut=70%
    37 Humble Store               USD  17.99  cut=70%
    61 Steam                      USD  17.99  cut=70%
    36 GreenManGaming             USD  18.00  cut=70%
    49 Newegg                     USD  59.99  cut=0%
monedas: {'USD': 10}
```

**10 de 10 ofertas en USD, y las cuatro tiendas oficiales con el mismo importe.** Eso no es un
precio mexicano: es el precio de EE.UU. proyectado sobre todas las tiendas. El `cut` (70 %) sí es
regional y coincide con el real, así que el **orden relativo sobrevive**; lo que no sobrevive es el
importe.

Contraste con la realidad MX, medida hoy con las APIs nativas:

| Tienda | Precio MX real | Regular | Descuento | Fuente |
|---|---|---|---|---|
| **Epic** | **MXN 161.99** | MXN 539.99 | −70 % | `store.epicgames.com/graphql` |
| Microsoft/Xbox | MXN 419.70 | MXN 1,399.00 | −70 % | `apps.microsoft.com/api/products/search` |
| Steam | MXN 419.70 | MXN 1,399.00 | −70 % | `store.steampowered.com/api/appdetails` |
| ITAD (todas) | ≈ MXN 327 (USD 17.99 × FIX) | — | −70 % | `prices/v3` |

Consecuencias concretas, sin interpretación:

1. **Epic MX a MXN 161.99 es invisible.** ITAD lo publica a USD 17.99 → la UI lo convierte a ≈ MXN 327
   con badge `fx_estimate`. Gana Steam (419.70) en la comparación cuando en realidad Epic lo está
   vendiendo a un **38 % de ese precio**. Es exactamente el caso que reportaste.
2. **Hoy hay una fila de Steam contradictoria.** Steam se consulta directo (419.70, correcto) y además
   ITAD devuelve shop 61 a USD 17.99 (≈ 327). Son dos filas distintas (`source='steam'` y
   `source='itad'`, `offer_key` = shopId 61), así que la ficha puede mostrar dos precios de Steam que
   se contradicen. **Bug preexistente, independiente de esta fase.**
3. **El `OFFICIAL_SHOP_IDS` no es el problema.** 16 y 48 ya están en el allowlist. El problema es la
   calidad del dato, no el filtro.
4. Si además la oferta no aparece del todo en la ficha, la causa es el gate de 7 días
   (`offers_refreshed_at`) o el snapshot, no la ausencia de la tienda. Verificar en runtime.

**Conclusión de arquitectura:** ITAD deja de ser fuente de precio y pasa a ser **resolvedor de
identidad y enlaces** (para lo que es excelente: sin key, bidireccional, verificado) más **fallback**
para tiendas que no podamos tarificar (Ubisoft). El precio lo da la tienda.

### 2.1 El WAF de Epic discrimina por huella TLS, no por IP ni por User-Agent

Síntoma: las mismas peticiones que devolvían 200 desde la máquina de desarrollo devolvían un
challenge de Cloudflare (`challenge-platform`) desde el servidor. Siete clientes, dos hosts, **una
sola IP pública** (`189.130.183.226`, comprobada en ambos):

| Cliente | Host | libssl del sistema | Epic `/graphql` con UA `DealsExt/1.0 (+https://amitzi.xyz)` |
|---|---|---|---|
| curl 8.22.0 | CachyOS | 3.6.4 | **200** |
| .NET 9.0.19 | CachyOS | 3.6.4 | **200** |
| curl 8.5.0 | Ubuntu 24.04 (host) | 3.0.13 | 403 |
| .NET 9.0.20 | Ubuntu 24.04 (host) | 3.0.13 | 403 |
| .NET 9.0.15 | contenedor `aspnet:9.0` (bookworm) | **3.0.19** | 403 |
| .NET 9.0.20 | contenedor `aspnet:9.0-azurelinux3.0` | **3.3.7** | **200** |
| .NET 9.0.20 | contenedor `aspnet:9.0-alpine` (3.24) | 3.5.x | **200** |

Qué descarta cada control, porque la conclusión depende de tenerlos todos:

- **Misma IP pública en los dos hosts** → no es reputación de IP.
- **El servidor no tiene IPv6** → no es familia de dirección.
- **Los tres contenedores corren sobre el mismo kernel** → no es fingerprint TCP/kernel (p0f).
- **`azurelinux3.0` (glibc) y `alpine` (musl) pasan ambos** → no es la libc ni el userspace en bloque.
- **Mismo .NET 9.0.20, mismo cuerpo, mismo `HTTP/1.1`, mismo User-Agent** → no es la petición.
- **`aspnet:9.0` medido como control y vuelto a dar 403** → la clasificación del WAF es estable, no aleatoria.

Lo único que correlaciona con el veredicto es **la versión de OpenSSL**: 3.0.x recibe challenge, ≥3.3
pasa. El mecanismo probable es la lista de cifrados y extensiones del ClientHello, que cambió entre
3.0 y 3.2; el detalle exacto no hace falta para decidir. **No hace falta invocar ML-KEM ni
post-cuántica:** 3.3.7 pasa sin llevar key share híbrido, así que la hipótesis "hay que parecerse a
Chrome 2026" queda descartada por medición.

Consecuencias operativas, que es lo que se lleva la fase:

1. El User-Agent descriptivo es **necesario pero no suficiente**: sin él hay 403 en todos los stacks
   (medido), con él el veredicto depende del host.
2. `curl` desde el host **no es un oráculo válido** para lo que hará la app. El cliente real es
   `HttpClient` dentro del contenedor, con su propio OpenSSL. Regla: medir dentro de la imagen de
   runtime, nunca desde el host.
3. Impersonar un navegador no compra nada en este eje: en el host Ubuntu el UA de Chrome también da
   403. La palanca es la libssl, no las cabeceras.

**Arreglo adoptado:** la etapa de runtime de `Dockerfile.api` pasa de `aspnet:9.0` (bookworm, libssl
3.0.19) a **`aspnet:9.0-azurelinux3.0`** (glibc, libssl 3.3.7), medido en 200. Se eligió sobre
`-alpine` (también 200) para no cambiar de libc. La etapa de build no cambia: el publish es
*framework-dependent*, así que la imagen de runtime es lo único que decide.

## 3. Por qué IGDB no es la matriz

Cuatro razones, ninguna opinable:

1. **Ya hay un camino verificado y gratis.** `lookup/id/shop/{id}/v1` mapea Epic, GOG y Microsoft a
   identidad canónica hoy, sin key ni OAuth. IGDB entregaría lo mismo con más fricción.
2. **IGDB no está en el camino del precio.** Epic responde precio por keyword y Microsoft por
   PackageFamilyName/StoreId. Ninguno de los dos necesita un grafo externo para tarificar.
3. **Coste real:** OAuth2 contra Twitch, 4 req/s, licencia **solo uso no comercial** (ya anotada como
   riesgo en `PLAN_CATALOG.md` §6). Es un impuesto de integración permanente.
4. **La identidad no es el cuello de botella.** El cuello de botella es que ITAD no tiene el precio MX.

**Decisión:** IGDB queda **fuera de alcance**, igual que HLTB. Si algún día hace falta metadato
(portada, fecha de lanzamiento, `first_release_date` para desambiguar remasters), es aditivo con el
esquema actual: `game_external_ids(namespace='igdb')` ya está en el vocabulario y `games` ya tiene
las columnas. No hay que preparar nada ahora.

## 4. Qué falta por tienda

### 4.1 Epic — resuelto, keyless, MXN nativo

```
POST https://store.epicgames.com/graphql
{"query":"query($keywords:String!,$country:String!,$locale:String!){Catalog{searchStore(
   keywords:$keywords,country:$country,locale:$locale,count:N){elements{
   title id namespace offerType price(country:$country){totalPrice{
   discountPrice originalPrice currencyCode}}}}}}",
 "variables":{"keywords":"Octopath Traveler","country":"MX","locale":"es-MX"}}
```

Devuelve `title`, `id` (= offerId), `namespace` (= sandboxId), `offerType`,
`price.totalPrice.discountPrice` y `originalPrice` **en unidades menores enteras con `currencyCode`**
(`16199` / `53999` / `MXN`), que es la convención `_minor` del repo tal cual.

Y por oferta individual, con el `id` exacto:

```
Catalog{catalogOffer(namespace:"<sandboxId>", id:"<offerId>", locale:"es-MX"){
  id title offerType developerDisplayName publisherDisplayName urlSlug
  price(country:"MX"){totalPrice{discountPrice originalPrice currencyCode}}}}
```

Verificado: devuelve `f51ebf66eb42420bba6cdd7d87cf56af`, `OCTOPATH TRAVELER™`, publisher
`Square Enix`, `urlSlug: octopath-traveler`, **MXN 161.99 / 539.99**.

- **Identidad:** `('epic', urlSlug)` — decidido al implementar la Fase 1. El `offerId` también lo acepta
  ITAD, pero el slug es lo que devuelve la redirección y lo único que la tienda vuelve a resolver a una
  oferta. El `offerId` se persiste en `game_offers.shop_id`, que es donde vive el artefacto de la tienda.
- **Enlace:** `https://store.epicgames.com/<locale>/p/<slug>` — construido desde el slug validado, nunca
  copiado del payload.
- **Gotcha:** el argumento es **`id`**, no `offerId`. Y el `productId` que devuelve
  `StorePageMapping` **no** es el offerId: `catalogOffer` con él da `offer_not_found`. Se distinguen
  por la respuesta: `searchStore.elements[].id` y `catalogOffer.id` sí son el mismo valor.
- **Gotcha:** el `GameId` de Playnite para Epic es el **AppName** (`Ovenbird`), que no sirve ni para
  ITAD ni para Epic. Para juegos poseídos hay que resolverlo por la cadena exacta de §4.5.
- **Gotcha:** no hay introspección GraphQL (Apollo con `introspection: false`). Los nombres de campo
  solo se descubren por error de validación. Fijarlos en un test de contrato.

### 4.2 Microsoft / Xbox — resuelto, keyless, MXN nativo

```
GET https://apps.microsoft.com/api/products/search?query=<título>&hl=es-MX&gl=MX
```

Una sola llamada devuelve todo lo necesario: `productId` (= StoreId, minúsculas), `packageFamilyNames`,
`price` (decimal), `displayPrice`, `strikethroughPrice`, `priceInfo.badgeText` (`-70%`),
`isGame`, `productFamilyName`, `encodedTitle` (slug), `redirectUrl`.

Y para el camino inverso, el id que ya tenemos en la biblioteca:

```
GET https://displaycatalog.mp.microsoft.com/v7.0/products/lookup
      ?alternateId=PackageFamilyName&value=MotionTwin.DeadCellsWin10_rtjy889c6zgtg
      &fieldsTemplate=Details&market=MX&languages=es-mx
→ ProductId 9NKVX66J0ZSK, MXN 439.00
```

**Los 348 `GameId` de Xbox del export de Playnite son PackageFamilyName** (p. ej.
`MotionTwin.DeadCellsWin10_rtjy889c6zgtg`). Esto resuelve de una vez la deuda que
`PLAN_LIBRARY.md` §5 dejó abierta: esos juegos sí tienen identidad apta para precios, keyless y en
MXN. Ya no hay motivo para excluirlos por `state='subscription'`, salvo el semántico de "Game Pass no
es posesión" (que sigue siendo correcto como etiqueta, no como bloqueo de precio).

- **Identidad canónica:** `('xbox', storeId_en_minúsculas)`, que es el formato que ITAD acepta.
  La PackageFamilyName vive en `user_library.store_game_id` y se resuelve al StoreId cuando haga falta.
- **Enlace:** el `redirectUrl` que ya viene en la respuesta.
- **Gotcha de unidades:** Microsoft entrega **decimal** (`419.7`), Epic entrega **entero menor**
  (`41970`). Un solo conversor en el borde: `Round(x * 100, AwayFromZero)`. No mezclar.
- **Gotcha de SKUs:** `DisplaySkuAvailabilities` trae SKUs con precio `0.0` y `actions` de tipo
  `License`. No son el precio de venta. Regla: tomar el SKU con `Purchase` en `Actions` y `ListPrice`
  distinto de cero. En el caso medido, el SKU `0010` a MXN 1599 con `WholesalePrice 964.92` era el
  comprable; los `0.0` eran licencias de suscripción/Game Pass.
- **Gotcha:** `products/lookup` no acepta la sintaxis `clave=valor`; exige `alternateId` + `value`.
- **Gotcha:** `alternateId=ProductId` **no existe**: devuelve 0 resultados incluso con un id válido
  (`9NKVX66J0ZSK`, medido). El único camino de identidad que funciona es `alternateId=PackageFamilyName`.
  Un StoreId suelto no se puede tarificar directo; requiere el search filtrado por `productId` exacto.
- **Gotcha:** el response de `products/lookup` trae `Products` (array, P mayúscula) y **no** `Product`.
  `Products[0].ProductId` viene en **mayúsculas** mientras que el search lo da en minúsculas: hay que
  normalizar a minúsculas antes de persistir, que es lo que ITAD acepta.

#### Correcciones medidas después (implementación de la Fase 2)

Una batería de sondas de solo lectura sobre la forma real del JSON, que ajusta lo de arriba:

| Medición | Resultado |
|---|---|
| `displaycatalog?alternateId=ProductId&value=9NKVX66J0ZSK` | **0 resultados** (también en minúsculas). Ese `alternateId` no sirve |
| `apps.microsoft.com/api/products/search?query=<PFN>` | `totalCount: 0` y una lista de 10 productos **no relacionados**. El search es **solo por título** y sus resultados **nunca** son un match sin filtro por id exacto |
| Dónde vive el precio | `Products[0].DisplaySkuAvailabilities[].Availabilities[].OrderManagementData.Price.{CurrencyCode,ListPrice,MSRP}` |
| Regla de SKU | filtrar `Actions` con `Purchase` **y** `ListPrice > 0` |
| Dead Cells (`0010`) | `ListPrice=439.0`, `MSRP=439.0` (sin descuento). `WholesalePrice=307.3` **no** es el precio del usuario |
| OCTOPATH TRAVELER (`0010`) | `ListPrice=419.7`, `MSRP=1399.0` → descuento `1 − ListPrice/MSRP` = **−70 %**, igual que el badge de la tienda |
| Cities: Skylines (en suscripción) | **una** availability `Purchase` a 709/709 **más dos a 0.0/0.0**. El síntoma "Incluido" de arriba es del *search* (`price: 0`); en `displaycatalog` aparece como esas filas a 0. Las dos se resuelven con la misma regla: `ListPrice > 0` |
| `Products[0].PreferredSkuId` | `0010` en los tres casos medidos: selecciona el SKU |
| `deal_url` sin slug ni título | `https://apps.microsoft.com/detail/{storeId_en_minúsculas}` → **200** y redirige a la URL canónica `www.xbox.com/{locale}/games/store/{slug}/{id}`; un id falso → **410** `ProductNotFound`. `www.xbox.com/{locale}/games/store/-/{id}` también resuelve (404 con id falso) pero necesita el guion como slug |

### 4.3 Ubisoft — sin scraper, con enlace de revisión manual

`store.ubisoft.com` responde **302** a una página de challenge de Akamai en la raíz, en `/graphql`, en
`/api/search` y en `/mx/**`. `public-ubiservices.ubi.com/v3/profiles` devuelve 400 exigiendo
`Ubi-AppId`, y el resto de esa API es de ticket autenticado.

**No hace falta resolverlo, porque el catálogo Ubisoft ya está en las otras dos tiendas.** Medido:

| Sonda | Resultado |
|---|---|
`Epic searchStore("Assassin's Creed Mirage")` | `publisherDisplayName: "Ubisoft"`, `developerDisplayName: "Ubisoft"`, `seller.name: "Ubisoft Entertainment"` |
`apps.microsoft.com search("assassin's creed mirage")` | `publisherName: "Ubisoft"` en las 3 ediciones |
`ITAD prices/v3` de AC Mirage | devuelve **shop 62 Ubisoft Store** (`USD 15`, o sea mismo problema de moneda que §2) |

Por tanto la decisión es:

| Opción | Veredicto |
|---|---|
| Scraper de `store.ubisoft.com` | **No.** Akamai activo, sin contrato, coste alto y frágil para 89 juegos de biblioteca de los que la mayoría ya está en Epic o Microsoft |
| Precio vía ITAD shop 62 | **No** como precio (USD de EE.UU., §2). Sí como enlace |
| **Detección por publisher + enlace de revisión manual** | **Sí, recomendada** |

**Cómo funciona la detección por publisher.** `Epic` y `Microsoft` ya devuelven el publisher en la
misma llamada de precio, así que no hace falta IGDB ni una fuente nueva: se añaden dos columnas
nullable a `games` y se rellenan oportunísticamente desde las respuestas que ya se piden.

```sql
ALTER TABLE games ADD COLUMN IF NOT EXISTS publisher VARCHAR(200) NULL;
ALTER TABLE games ADD COLUMN IF NOT EXISTS developer VARCHAR(200) NULL;
```

Regla de presentación, no de datos: si `publisher ILIKE '%ubisoft%'` y no hay oferta de Ubisoft Store
en la ficha, mostrar el botón **"Buscar en Ubisoft Store"** apuntando a la búsqueda del dominio
(`https://store.ubisoft.com/mx/` + término). No es una integración: es un enlace saliente para que tú
abras la tienda a mano.

> **Pendiente de confirmar a mano:** la ruta exacta de búsqueda de `store.ubisoft.com`. Las sondas
> automáticas reciben 302 de Akamai, así que la URL hay que abrirla una vez en un navegador real antes
> de fijarla. Si no se confirma, se usa el enlace a la raíz de la tienda MX.

El mismo `publisher` sirve para explicar en la UI por qué un juego solo aparece en dos tiendas, que es
información útil y gratis. **No** se convierte en un motor de reglas: es un dato de ficha.

### 4.4 Tiendas descartadas

| Tienda | Juegos en biblioteca | Sonda | Estado |
|---|---|---|---|
| **GOG** | 342 | `catalog.gog.com/v1/catalog?countryCode=MX` → **HTTP 200 sin key** | **Descartada por redundante, no por imposible.** GOG factura México **en USD** (la sonda devolvió `USD` con `countryCode=MX`), así que aporta un importe en USD igual que ITAD shop 35, que ya está en el allowlist. Cero ganancia a cambio de un cliente más. Su id de Playnite (`1443428641`) ya lo resuelve ITAD sin traducción, así que reactivarla después es barato |
| Amazon | 195 | sin PA-API utilizable (requiere credenciales de afiliado y aprobación); `/dp/<asin>` no es un id que ITAD conozca | **Bloqueada.** ITAD tampoco la cubre (`PLAN_LIBRARY.md` §3: 34 tiendas, ninguna de Amazon) |
| Humble | 39 | `humblebundle.com/store/api/search` → **HTTP 403** | Bloqueada directo; ITAD shop 37 la cubre en USD |
| Battle.net | 11 | sin API pública | Bloqueada directo; ITAD shop 4 existe |
| Ubisoft | 89 | Akamai 302 | Bloqueada directo; ver §4.3 |

Patrón: **toda tienda que no se puede tarificar directo ya tiene una fila de ITAD, a precio USD.**
Eso convierte a ITAD en la red de seguridad para enlaces y para lo que no se puede hacer mejor, nunca
en fuente de precio MX (§2).

### 4.5 Cadena de identidad exacta (sin coincidencia por nombre)

**No hay que buscar por nombre en cada tienda.** La coincidencia por título se evita con una cadena
verificada de cuatro pasos, y la mayor parte del coste ya se paga hoy:

```
appid de Steam
  └─(1) lookup/id/shop/61/v1   → UUID de ITAD            [sin key · ya implementado]
       └─(2) games/prices/v3   → deals[].url             [key · YA se llama y YA se persiste en deal_url]
            └─(3) seguir la redirección de itad.link      [1 request · una vez por juego y tienda]
                 └─(4) parsear la URL final             → id EXACTO de la tienda
```

Resultado medido de las tres redirecciones del mismo juego:

| Tienda | URL final tras `itad.link` | Id extraído |
|---|---|---|
| Epic | `www.epicgames.com/store/p/octopath-traveler?epic_game_id=1c4745021d4243a783b459d928b9088f` | slug `octopath-traveler` + `sandboxId` |
| Microsoft | `www.xbox.com/es-MX/games/store/octopath-traveler/9n9606cc950j` | **StoreId** `9n9606cc950j` (ya en minúsculas) |
| Steam | `store.steampowered.com/app/921570/OCTOPATH_TRAVELER` | `921570` (redundante, ya lo tenemos) |

Por qué es exacto y no heurístico:

- El id lo emite **la propia tienda** en la URL a la que ITAD redirige. No se adivina el slug.
- Para Epic, el `urlSlug` de `searchStore` se compara por igualdad estricta contra el slug de la URL
  (o se usa la clave `id` ya resuelta). La normalización de títulos no participa.
- Para Microsoft, el StoreId sale en la ruta de la URL final y ya viene en la capitalización que ITAD
  acepta.
- El resultado se guarda en `game_external_ids` **una vez** y no se vuelve a resolver.

**Coste real de identidad: cero llamadas de precio extra.** El paso (2) ya ocurre en la ficha del
juego y `deal_url` ya es una columna poblada. El paso (3) es un `HEAD` con seguimiento de redirección,
una sola vez por juego y tienda, y ya se pudo ver que no necesita key.

**Cuándo sí hay búsqueda por título** (y solo entonces):

1. ITAD no devuelve ninguna oferta de esa tienda (juego no cubierto, exclusivo, o lanzado hace poco).
2. Juego de la biblioteca con `game_id` pero sin appid de Steam: no hay paso (1) del que partir.

En ambos casos aplica el guard de igualdad estricta de §6 Fase 1: sin coincidencia exacta de título
normalizado no se escribe ni el id ni la oferta. Es un camino **de segunda**, no el principal.

## 5. El bloqueo estructural: `game_offers` está anclado a Steam

Sin resolver esto, nada de lo anterior se puede persistir. Verificado en `SQL/schema.sql`:

```sql
CREATE TABLE game_offers (
    steam_game_id INT NOT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
    ...
    CONSTRAINT uq_game_offers UNIQUE (steam_game_id, source, offer_key)
);
```

Un juego que solo existe en Epic o en Xbox **no tiene fila en `steam_games`**, por lo tanto no puede
tener ofertas. `PLAN_CATALOG.md` §3 ya dejó escrita la receta exacta y sigue siendo válida:

> añadir `game_id BIGINT` y `region VARCHAR(2)` a `game_offers`, rellenar `game_id` desde
> `steam_games.game_id`, y estrechar la clave única a `(game_id, region, source, offer_key)`.

Ese `region` es ahora **obligatorio**, no opcional: Epic y Microsoft se consultan por país y sus
precios MX y US son distintos (161.99 MXN vs 17.99 USD para el mismo juego). Sin `region` en la clave,
un snapshot de EE.UU. pisaría al de MX en silencio.

### 5.1 Veredicto sobre `steam_games`: se queda, y no es legacy

No es un duplicado de `user_library`. Son cosas distintas:

| | Qué guarda | De quién es |
|---|---|---|
| `games` | identidad del juego | compartida |
| `user_library` | "este usuario quiere/posee este juego" (`state='wishlist'` \| `owned` \| `subscription`) | del usuario |
| `steam_games` | **el snapshot de precio de Steam por región** + el gate de refresco + `itad_game_id` | del proveedor |

Evidencia de que sigue siendo pieza central, no residuo:

1. Es el destino de la FK de `game_offers`, `steam_price_observations` y `external_bundle_games`.
2. `WishlistSyncService` **ya lo usa como motor**: su pasada de refresco enriquece cada appid de
   wishlist desde el mismo snapshot `region='mx'` (comentario literal en `WishlistSyncService.cs:31`).
   La wishlist no tiene precios propios; los lee de aquí.
3. `region` pertenece a `steam_games` y es correcto que viva aquí: es una propiedad del snapshot.

Y **no estorba** exactamente por lo contrario de lo que sugiere el nombre: no es el catálogo, es la
tabla del proveedor Steam. `PLAN_CATALOG.md` §3 ya evaluó demolerlo (renombrar a `steam_game_prices`,
mover `name`/`type`/`image_url`/`is_free` a `games`) y lo descartó con razón: obliga a reescribir
`SteamGameService` (1400+ líneas), la entidad, `AppDbContext` y cada `game.AppId`, **sin ganancia
funcional**.

Lo único que cambia con multi-store es su **rol**: después de la Fase 0 las ofertas cuelgan de
`game_id`, así que `steam_games` se queda con appid ↔ region ↔ precio de Steam ↔ gates. Eso es una
responsabilidad limpia y más estrecha que la actual.

**Decisión:** se queda. El nombre solo es exacto si algún día se renombra la tabla a
`steam_game_prices`, y eso es cosmético: no entra.

## 6. Plan propuesto

### Fase 0 — re-anclaje de `game_offers` (prerrequisito duro) — **implementado**

| Archivo | Cambio |
|---|---|
| `SQL/migrations/2026-10-02_game_offers_canonical_anchor.sql` | Nuevo. Añade `game_id` + `region`, backfill desde `steam_games`, `region SET NOT NULL`, `uq_game_offers_canonical` y `idx_game_offers_game_region` |
| `SQL/schema.sql` | Fuente de verdad actualizada. El ancla se añade con `ALTER` después de crear `games`, porque `game_offers` se declara antes |
| `SQL/checks/game_offers_anchor.sql` | Nuevo. 4 invariantes que deben devolver 0 filas |
| `Deals.Models/Entities/GameOffer.cs` | `GameId` (nullable) y `Region` (`NOT NULL`) |
| `Deals.BusinessLogic/Context/AppDbContext.cs` | Índice único canónico + índice por `(game_id, region)` |
| `Deals.BusinessLogic/Services/SteamGameService.cs` | `GetOrCreateOffer` sella `GameId` y `Region` desde el snapshot de Steam |
| `Deals.BusinessLogic/Services/GameMergeService.cs` | Repunta `game_offers.game_id` junto a los demás referrers |

Hallazgos que cambiaron el plan durante la implementación:

1. **El merge tenía que entrar en la Fase 0, y no estaba previsto.** `game_offers.game_id` es un FK
   `NO ACTION`: si `GameMergeService` no lo repunta, el `DELETE FROM games` pasa a fallar con 23503 y
   **toda fusión posterior a esta migración queda rota**. Ahora repunta en el mismo bloque que
   `steam_games` y `user_library`.
2. **`steam_game_id` se queda `NOT NULL`.** El plan decía relajarlo; se descarta. Nada escribe una
   oferta sin Steam todavía, así que relajarlo ahora solo debilitaría el único constraint que dedupea
   hoy y permitiría filas sin ancla. Lo relaja la fase que sí necesita escribirlas.
3. **`region` es `NOT NULL`, no nullable.** Un `NULL` en la clave canónica la vuelve inaplicable
   (Postgres trata los `NULL` como distintos), que es justo el duplicado silencioso que esta migración
   existe para evitar. Todas las filas tienen `steam_game_id`, así que el backfill no deja ninguna.
4. **El orden de `schema.sql` importa.** `game_offers` se declara antes que `games`, así que la columna
   y sus índices van después del bloque `ALTER TABLE ... ADD COLUMN game_id`. Verificado con un chequeo
   estático de referencias adelantadas y de columnas usadas antes de existir.

Deudas que esta fase **crea** y que la Fase 2 paga (no son opcionales):

- `GetOrCreateOffer` busca la fila por la navegación `game.Offers`, que EF une por `steam_game_id`. Una
  oferta sin fila en `steam_games` no aparecería nunca en esa colección, así que cada refresco
  insertaría un duplicado. La Fase 2 la busca por `(game_id, region, source, offer_key)`.
- El merge no maneja colisiones en `game_offers`, y hoy no puede haberlas: su guardia permite a lo sumo
  un appid de Steam por juego, y toda oferta cuelga de un `steam_game_id`. En cuanto existan ofertas sin
  Steam, el superviviente puede tener la misma `(region, source, offer_key)` que el absorbido y hace
  falta el patrón de favoritos (borrar la colisión y luego repuntar).

No se podía saltar: es el único cambio que abre la puerta a todo lo demás y `PLAN_CATALOG.md` ya lo
preveía.

### Fase 1 — cadena de identidad + cliente de tienda genérico + Epic — **implementado y verificado en runtime**

**Primero la identidad, porque es lo que evita buscar por nombre.** La cadena implementada, en orden:

1. El `deal_url` de ITAD de la oferta de Epic (`shop_id = 16`) que ya se persiste.
2. Seguirlo una vez: `IItadClient.ResolveDealUrlAsync` devuelve la URL final de la tienda.
3. `EpicStoreClient.ExtractSlug` saca el slug del path `/p/<slug>` (tolera prefijo de locale).
4. `searchStore(keywords = título del juego)` y **solo** se acepta el elemento cuyo `urlSlug` es
   exactamente ese slug.

Decisiones tomadas al implementar, que corrigen lo que decía este plan:

- **El id canónico de Epic es el `urlSlug`, no el `offerId`.** Es lo que devuelve la redirección, es
  lo que ITAD acepta para shop 16 y es lo único que la tienda resuelve a una oferta. El `offerId`
  (`f51ebf66…`) va a `game_offers.shop_id`, que es donde vive el artefacto de la tienda.
- **`IStorePriceProvider` tiene un solo método, con id externo obligatorio.** El respaldo por título
  queda documentado y **no** implementado: sin slug no hay identidad, y una identidad adivinada es un
  precio equivocado. Menos superficie y cero riesgo de falso positivo. El respaldo se añade el día que
  falte precio de un juego que la tienda sí vende y ITAD no enlaza.
  ```csharp
  Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken ct);
  ```
- **La fila de Epic es única por `offer_key = 'store'`.** La tienda vende una sola oferta base por
  juego, así que una clave fija hace que un cambio de `offerId` **actualice** la fila en vez de dejar
  una segunda detrás.
- **`GetOrCreateOffer`, el merge y la relajación de `steam_game_id` se mueven a la Fase 2.** Las
  ofertas de Epic siguen colgando de su fila de `steam_games` (`steam_game_id NOT NULL`), así que
  ninguna de las tres deudas es alcanzable todavía: la navegación `game.Offers` las ve, y dos juegos
  distintos no pueden compartir `(region, source, offer_key)` porque cada juego tiene a lo sumo un
  appid de Steam. La que **sí** las necesita es la Fase 2, que escribe ofertas de juegos de Xbox que
  pueden no tener fila de Steam. Se pagan ahí, junto con el escritor que las exige.
- **`LookupByShopAsync` no se implementó** y se mueve a la Fase 3: la identidad de Epic sale de la
  redirección, y ITAD ya acepta el slug, así que la fase 1 no necesitaba una segunda llamada. Se
  añade cuando haga falta resolver un juego que no tenga appid de Steam, que es problema de la 3.

Hallazgos nuevos, medidos al implementar:

- **El User-Agent no es opcional.** `store.epicgames.com/graphql` está detrás de Cloudflare y
  responde **403 a una petición sin User-Agent** (`HttpClient` no manda ninguna por defecto) y también
  a agentes de bot conocidos (`curl/8.5.0`, `Mozilla/5.0` pelado). Un agente descriptivo
  (`DealsExt/1.0 (+https://amitzi.xyz)`) pasa. Reproducido y confirmado tres veces: sin agente 403,
  con agente 200 sobre el mismo payload. Configurable en `Epic:UserAgent`.
  **Corrección posterior, medida en el servidor:** el agente es necesario pero **no suficiente**. El
  WAF también decide por la libssl del cliente y hay hosts donde da 403 con cualquier agente. La frase
  "con agente pasa" solo vale para el stack donde se midió, que no es el de producción. Ver §2.1.
- **Las URLs de la tienda pueden llevar prefijo de locale** (`/es-MX/p/<slug>`). El parser del slug
  busca el segmento `/p/` en lugar de asumir su posición. Un parser que exigiera `/p/` en la primera
  posición habría rechazado justo las URLs que devuelve la redirección en México.
- **Un `itad.link` inválido termina en 404** en `isthereanydeal.com`. No es un fallo silencioso: el
  resolvedor lanza y la fase degrada conservando el snapshot.

Evidencia de que funciona, contra la tienda real (solo lectura, sin key):

| Comprobación | Resultado |
|---|---|
| `ExtractSlug` sobre 9 formas de URL (locale, `/home`, host ajeno, `http://`, sin `/p/`) | 9/9 correctas |
| `FindOfferAsync("OCTOPATH TRAVELER", "octopath-traveler")` | `OCTOPATH TRAVELER™`, `id=f51ebf66eb42420bba6cdd7d87cf56af`, **16199/53999 MXN**, −70 %, URL `store.epicgames.com/es-MX/p/octopath-traveler` |
| `FindOfferAsync("OCTOPATH TRAVELER", "octopath-traveler-ii")` | sin oferta (guard de slug exacto) |
| `ResolveDealUrlAsync` con un `itad.link` real | sigue la cadena y devuelve la URL final |
| `ResolveDealUrlAsync` con host ajeno o vacío | `null`, nunca sigue a un host arbitrario |

Migración: `SQL/migrations/2026-10-03_epic_store_offers.sql` (solo `steam_games.epic_refreshed_at`; las
filas de Epic son ofertas normales, sin columnas propias).

**Verificación en producción (hecha):** desplegado el contenedor con la imagen de runtime
`aspnet:9.0-azurelinux3.0`, la ficha de OCTOPATH TRAVELER muestra el grupo *Epic Games Store* con
**MXN 161.99**. Ese importe no puede venir de ITAD — que para ese juego estimaba ≈ MXN 327 vía
`fx_estimate` — así que sale del GraphQL de Epic: la cadena de identidad, el cliente y el arreglo de
la huella TLS funcionaron a la vez. Queda cerrada la pregunta que este plan dejó abierta en §2.1.

### Fase 2 — Microsoft / Xbox

- **Pagar las tres deudas que la Fase 1 no podía alcanzar**: buscar la oferta por
  `(game_id, region, source, offer_key)` en `GetOrCreateOffer`, manejar la colisión de `game_offers` en
  `GameMergeService` (borrar la colisión y luego repuntar, como los favoritos) y relajar
  `steam_game_id` a `NULL`-able en su propia migración. Es esta fase, y no la 1, porque es la primera
  que escribe la oferta de un juego que puede no tener fila de Steam.
- `MicrosoftStoreClient`, **un solo camino de identidad**, que es lo que las sondas permiten
  (§4.2 *Correcciones medidas después*): `GetByStoreIdAsync` **no existe** como tal, y el `search`
  necesita título. El camino implementable es **PackageFamilyName → `products/lookup`**, que devuelve
  en la misma respuesta el `ProductId` (el id canónico) y el precio. El respaldo por título con filtro
  de id exacto se **pospone**: solo hace falta para identidades que vengan de un `deal_url` de ITAD sin
  PFN, que es material de la Fase 5.
- `source='microsoft'`, `classification='official'`, `pricing_type='regional'`, MXN nativo.
- Identidad: `('xbox', storeId_en_minúsculas)` — el response lo da en mayúsculas y se normaliza.
- Precio MX para los 348 juegos de Xbox de la biblioteca vía su PackageFamilyName. Cambiar la
  exclusión por `state='subscription'` de "sin precio" a "con precio, sin badge de propiedad".
  **Ojo:** hoy `LibraryPriceBindingService` corta en `state='subscription'` como "nunca una consulta"
  (paso 1 de `ResolveAsync`), así que la decisión toca ese servicio, no solo la UI.
- Gotcha medido: un título incluido en suscripción devuelve `displayPrice: "Incluido"` y `price`
  nulo (AC Mirage base). Sin importe numérico no se escribe oferta. En `products/lookup` ese mismo caso
  llega como availabilities `Purchase` extra a `0.0`, así que la regla única es `ListPrice > 0`.

### Fase 3 — limpieza de ITAD

| Cambio | Razón |
|---|---|
| Quitar `61` de `ITAD__OfficialShopIds` | Steam se consulta directo; la fila de ITAD lo contradice (bug §2.2). Elimina la duplicación sin código nuevo |
| Marcar toda oferta de ITAD como `pricing_type='fx_estimate'` y `source='itad'` | Es la verdad: no es un precio MX, es una estimación |
| `LookupByShopAsync` + resolvedor de `itad.link` | Es el resolvedor de identidad cross-store, sin key. Formatos ya verificados (§1, §4.5) |
| Mantener ITAD para Ubisoft, Humble, Battle.net y GOG, y como fallback | Es lo único que cubre tiendas sin API propia |

### Fase 4 — publisher/developer y enlace de revisión manual de Ubisoft

- Migración aditiva: `games.publisher`, `games.developer` (nullable).
- Rellenado oportunístico desde Epic (`publisherDisplayName`, `developerDisplayName`) y Microsoft
  (`publisherName`), en las llamadas de precio que ya se hacen. Cero peticiones extra.
- Botón "Buscar en Ubisoft Store" cuando el publisher es Ubisoft y no hay oferta de shop 62.
- Confirmar a mano la URL de búsqueda de Ubisoft MX antes de fijarla (§4.3).

### Fase 5 — job multi-tienda dirigido por la wishlist

La wishlist ya existe y ya tiene job. No se crea un scheduler nuevo: se extiende `wishlist.sync`
(`WishlistSyncService`, `JobRunLog.WishlistSync`), que hoy tiene dos pasadas — `SyncListAsync` (lista de
Steam) y la de refresco de precios.

| Paso | Detalle |
|---|---|
| 1 | La pasada de lista no cambia: sigue trayendo appids de Steam y sus filas en `user_library(state='wishlist')`. |
| 2 | Nueva pasada de **identidad**: por cada fila con `game_id`, si falta `('epic', …)` o `('xbox', …)`, resolver con la cadena exacta de §4.5 (UUID → `deal_url` → seguir redirección → id). La búsqueda por título es el respaldo, no el camino. Se persiste el mapeo. Coste: **una vez por juego**, no por ciclo. |
| 3 | Nueva pasada de **precio**: solo para ids ya resueltos. Epic y Microsoft, en MXN nativo. |
| 4 | Gate de refresco propio por tienda y por juego (mismo patrón que `offers_refreshed_at` / `ggdeals_refreshed_at`), para que el job no vuelva a pedir lo mismo cada noche. |

Presupuesto: con `ProviderRequestGovernor` (token bucket 1 req/s y burst 10) una wishlist de 600
juegos son ~20 min por pasada completa, en background y sin tocar el camino de request del usuario. El
gate por tienda hace que el caso normal sea casi todo cache-hit. La pasada de identidad es
**mucho más barata que la de precio**, porque un `HEAD` de redirección y una búsqueda no son lo mismo
que una llamada de precio por tienda, y además el mapeo queda guardado.

Regla de orden que evita saturar: **primero todos los títulos, después todos los precios.** Mezclados,
un juego con identidad sin resolver reintentaría la búsqueda en cada ciclo.

`PriceAlertScheduler` (`PLAN_WISHLIST.md` §7) consume el resultado después; con precios MX reales, la
alerta de Epic se vuelve útil por primera vez.

### Fase 6 — UI

- Badge por fila: **`Precio regional MX`** (Epic, Microsoft, Steam directo) vs **`Estimado`**
  (ITAD/FX). Ya existe el vocabulario `regional | fx_estimate | unconverted`.
- Enlace "Ver en la tienda" con el `deal_url` propio de cada tienda (no el `itad.link`), para Epic y
  Microsoft.
- La tabla de ofertas ya está ordenada por precio; con precios MX reales, Epic aparecerá arriba solo.

### Orden obligatorio

1. ~~Commitear el trabajo de ITAD y FX~~ — **hecho y verificado**: entró en `05fff68` y `80b7e5f`, y
   las migraciones `2026-09-17_itad_offers` / `2026-09-18_fx_rates` ya están en `HEAD`. Nada
   pendiente por ese lado; la nota de `PLAN_ITAD.md` §5 que decía lo contrario se corrigió.
2. Fase 0.
3. Fases 1 y 2 (independientes entre sí).
4. Fases 3, 4 y 5.
5. Fase 6 (depende de que las ofertas multi-tienda ya se escriban).

## 7. Esfuerzo estimado

| Fase | Alcance | Estimación |
|---|---|---|
| 0 | 1 migración fechada + `GameOffer` + `AppDbContext` + escritores | 4–6 h — **hecha** |
| 1 | `IStorePriceProvider`, `EpicStoreClient`, resolvedor de `itad.link` | 6–8 h — **hecha** |
| 2 | `MicrosoftStoreClient`, StoreId + PackageFamilyName, conversión decimal→menor | 6–8 h |
| 3 | Config + `LookupByShopAsync` + `pricing_type` | 2–3 h |
| 4 | 2 columnas + rellenado oportunístico + botón Ubisoft | 2–3 h |
| 5 | Pasadas de identidad y precio dentro del job de wishlist | 4–6 h |
| 6 | BFF + contratos + badges + enlaces | 4–6 h |

GOG, si algún día se reactiva: ~6 h (tercer `IStorePriceProvider`; su resolución de id ya la da ITAD).

Librerías nuevas: **ninguna**. `HttpClient` + `System.Text.Json`, que es lo que ya usa `ItadClient`.
Nada de SDKs de terceros para GraphQL ni para Microsoft Store.

## 8. Riesgos

| Riesgo | Mitigación |
|---|---|
| **Las APIs de Epic y Microsoft son internas, no documentadas y sin SLA.** No hay contrato, pueden cambiar o bloquear por IP | Aislar en `IStorePriceProvider`; degradación a "sin precio de esa tienda"; nunca romper la ficha; validar el contrato con un test que falle ruidosamente |
| **Cloudflare delante de la API de Epic** | Medido: 403 sin User-Agent y con agentes de bot conocidos; 200 con un agente descriptivo. `Epic:UserAgent` es obligatorio y validado al arrancar. Un 403 degrada a "sin precio de Epic" conservando el snapshot, y el gate de 7 días mantiene el volumen en ~1 llamada por juego y semana |
| **El WAF de Epic vuelve a marcar la huella TLS del contenedor** | La versión de OpenSSL del runtime decide el veredicto (§2.1) y no está bajo nuestro control. Mitigación: la imagen de runtime se eligió por eso y el motivo queda escrito en `Dockerfile.api`; un 403 no rompe la ficha. Residual: si vuelve a bloquear, la salida es volver a medir imagen por imagen, y si ninguna pasa la degradación ya existe. Lo que **no** se hace es escribir un interruptor de configuración preventivo ni un `Epic:Enabled` que hoy nadie necesita |
| Fricción de ToS por consumo automatizado | Volumen bajo (una llamada por juego y refresco), `ProviderRequestGovernor`, User-Agent honesto. No hay scraping de HTML en ninguna ruta propuesta |
| Un falso positivo de título escribe el precio de otro juego | **La coincidencia por título no es el camino principal** (§4.5); solo el respaldo, y con guard de igualdad estricta. Un desajuste no escribe |
| `region` mal poblada pisa snapshots MX con US | La clave única debe incluir `region` **antes** de escribir la primera oferta no-Steam |
| Microsoft devuelve SKUs `0.0` y de licencia | Regla explícita: `Purchase` en `Actions` y `ListPrice > 0` |
| Ubisoft: sin dato posible | Diferido y declarado, no simulado |
| El `™` y los sufijos de edición rompen el guard de título | Test de normalización con los valores reales medidos (`OCTOPATH TRAVELER™`, `OCTOPATH TRAVELER 0 Digital Deluxe Edition`) |
| El esquema de las URLs de `itad.link` cambia y la cadena exacta deja de parsear | El id extraído se valida contra la tienda antes de persistirlo (una llamada por juego, una vez). Si la URL final no encaja con el patrón conocido, la identidad queda sin resolver en vez de escribir un id dudoso. El respaldo por título sigue disponible
| La cadena de `itad.link` termina en un host distinto según el cliente | Medido: para un cliente sin navegador termina en `www.epicgames.com/store/p/<slug>`, no en `store.epicgames.com/<locale>/p/<slug>`. El parser se ancla en el segmento `/p/` y acepta cualquier subdominio de `epicgames.com`, rechazando hosts que solo lo imitan (`store.epicgames.com.evil.example`). Verificado con 14 formas de URL |
| Cloudflare bloquea la **página** de la tienda (403) aunque la redirección ya diera la URL | El resolvedor no juzga el estado final: la cadena de redirecciones es la información, el documento final no. Un 403 en el último salto es lo esperado y no impide extraer el slug. Solo un fallo de transporte degrada a "conservar el snapshot" |
| Microsoft devuelve precios no numéricos para títulos incluidos en suscripción | Medido: AC Mirage base se anuncia como `displayPrice: "Incluido"` con `price` nulo. Regla: sin precio numérico no se escribe oferta |
| La detección por publisher se convierte en un motor de reglas | Es un dato de ficha y un enlace saliente. Ninguna decisión de precio depende de `publisher` |
| El job de identidad reintenta la búsqueda de título en cada ciclo | Regla de orden de la Fase 5: identidad primero, precios después, y el mapeo se persiste |

## 9. Fuera de alcance

- IGDB como matriz de identidad (decisión razonada en §3). IGDB queda solo como metadato futuro.
- HLTB (`PLAN_CATALOG.md` §6 sigue vigente).
- GOG: **descartada por redundante** (§4.4). No es una fase pendiente, es una decisión: su precio es en USD y ya lo devuelve ITAD shop 35.
- Ubisoft con precio propio: scraper o navegador headless (§4.3).
- Amazon, Humble y Battle.net como precio propio (§4.4): bloqueadas, y con pocos juegos de biblioteca.
- Nintendo, EA: ni se evalúan.
- Reescritura de `SteamGameService` o demolición de `steam_games`.
- Auto-fusión de filas `games` por título.

## 10. Verificación

| Fase | Evidencia |
|---|---|
| 0 | `SELECT count(*) FROM game_offers WHERE game_id IS NULL` = 0; re-ejecutar el backfill no cambia nada; la ficha de Steam muestra el mismo precio que antes |
| 1 | OCTOPATH TRAVELER devuelve oferta Epic **MXN 161.99** con `pricing_type='regional'`; el `game_id` es el mismo que el de Steam (`app/921570`) — **verificado contra la tienda** |
| 1 | `searchStore` devuelve `OCTOPATH TRAVELER™` con **MXN 161.99**, `urlSlug: octopath-traveler`, `id=f51ebf66…` — **verificado** |
| 1 | Seguir el `deal_url` de ITAD de Epic resuelve a `www.epicgames.com/store/p/octopath-traveler?epic_game_id=…` y de ahí sale el slug: sin coincidencia por título — **verificado con un `itad.link` real** |
| 1 | Un título con parecido engañoso (`OCTOPATH TRAVELER 0`) **no** escribe oferta en la ficha de `OCTOPATH TRAVELER` — **verificado** con un slug ajeno |
| 1 | `SQL/checks/game_offers_anchor.sql` devuelve 0 filas, incluida la consulta 6 (ninguna fila de Epic con FX) — **verificado en producción** |
| 2 | Dead Cells desde el export de Playnite: `('xbox', 9nkvx66j0zsk)` resuelto y oferta **MXN 439.00** regional |
| 3 | Ninguna fila con `source='itad'` y `shop_id='61'`; toda fila de ITAD con `pricing_type='fx_estimate'` |
| 4 | AC Mirage tiene `publisher='Ubisoft'` rellenado sin ninguna petición extra, y muestra el botón a Ubisoft Store |
| 5 | Correr el job dos veces: la segunda no emite ninguna resolución de identidad (todo cache-hit en `game_external_ids`) y solo refresca los precios con el gate vencido |
| 5 | La wishlist de 600 juegos produce ofertas Epic y Microsoft en MXN, con `pricing_type='regional'` |
| 6 | La ficha muestra Epic arriba con 161.99 y badge "Precio regional MX" |

Sondas de reproducción del análisis (§1 y §2), todas sin key salvo la última. **Todas necesitan un
User-Agent**: sin agente, el endpoint de Epic responde 403 (ver §6 Fase 1).

> Ojo con las sondas de Epic: el veredicto del WAF depende de la **libssl del cliente que las lanza**,
> no del host donde se escriben (§2.1). Lanzadas desde el host Ubuntu dan 403 con o sin agente, y eso
> **no** dice nada sobre la app: el cliente real es `HttpClient` dentro del contenedor. Si hay que
> comprobar Epic, se comprueba **dentro de la imagen de runtime** (`aspnet:9.0-azurelinux3.0`).

```bash
UA='DealsExt/1.0 (+https://amitzi.xyz)'

# Epic MX, MXN nativo — el endpoint devuelve 403 sin User-Agent
curl -s -A "$UA" -X POST https://store.epicgames.com/graphql -H 'Content-Type: application/json' \
  -d '{"query":"query{Catalog{searchStore(keywords:\"Octopath Traveler\",country:\"MX\",locale:\"es-MX\",count:5){elements{title urlSlug id offerType price(country:\"MX\"){totalPrice{discountPrice originalPrice currencyCode}}}}}}"}'

# Microsoft MX por PackageFamilyName (el GameId de Playnite)
curl -s "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&value=MotionTwin.DeadCellsWin10_rtjy889c6zgtg&fieldsTemplate=Details&market=MX&languages=es-mx"

# Epic MX por oferta exacta
curl -s -A "$UA" -X POST https://store.epicgames.com/graphql -H 'Content-Type: application/json' \
  -d '{"query":"query{Catalog{catalogOffer(namespace:\"1c4745021d4243a783b459d928b9088f\",id:\"f51ebf66eb42420bba6cdd7d87cf56af\",locale:\"es-MX\"){id title urlSlug price(country:\"MX\"){totalPrice{discountPrice originalPrice currencyCode}}}}}"}'

# Cadena de identidad exacta: seguir el deal_url de ITAD hasta el slug.
# Ojo: el último salto responde 403 (Cloudflare sobre la página de la tienda). La URL final sirve igual:
# el resolver no mira el estado, solo la cadena de redirecciones.
curl -sL -o /dev/null -w '%{url_effective}\n' "https://itad.link/018d959e-e79a-7189-a071-bc4f582ff378/?app=wrzdeb"

# Microsoft MX por título
curl -s "https://apps.microsoft.com/api/products/search?query=octopath%20traveler&hl=es-MX&gl=MX"

# ITAD: identidad cross-store, sin key
curl -s -X POST https://api.isthereanydeal.com/lookup/id/shop/16/v1 -H 'Content-Type: application/json' -d '["f51ebf66eb42420bba6cdd7d87cf56af"]'

# ITAD: precio MX (requiere key en el entorno; nunca imprimirla)
curl -s -X POST "https://api.isthereanydeal.com/games/prices/v3?country=MX" \
  -H "ITAD-API-Key: $ITAD__ApiKey" -H 'Content-Type: application/json' \
  -d '["018d937f-07e9-7041-8d73-511c3bb2c94f"]'
```

Comandos del repositorio:

```bash
dotnet build Deals.sln
cd Deals.Web && pnpm build
```
