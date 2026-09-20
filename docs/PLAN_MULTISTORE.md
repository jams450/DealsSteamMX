# DealExt: plan multi-tienda (Epic / Xbox / Ubisoft)

Estado: **Fases 0, 1 y 2 implementadas, desplegadas y verificadas en producción** (Epic y Microsoft
devuelven MXN nativo en la ficha; Microsoft por sus dos caminos de identidad, §4.2). Fase 1 verificada con
el commit `865ec95`; migraciones `2026-10-02` y `2026-10-03` aplicadas a mano y
`SQL/checks/game_offers_anchor.sql` en 0 filas. Fase 2 verificada tras corregir `ItadMicrosoftShopId`
(`62` Ubisoft → **`48`** Microsoft, §8), con `2026-10-04`, `2026-10-05` aplicadas. Fases 3 a 6 pendientes,
pero **la 3 quedó casi vacía y la 5 es más pequeña de lo que decía este plan** — ver ambas secciones para
lo medido en código, no estimado.

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
`https://api.isthereanydeal.com/service/shops/v1?country=MX` | 200 sin key: **34 tiendas**, incluye 16 (Epic), 48 (Microsoft), 62 (Ubisoft), 61 (Steam). **Este es el mapeo de referencia para las constantes `Itad*ShopId` de `SteamGameService`** — se copian de aquí, no de memoria |
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

**De los 348 `GameId` de Xbox del export de Playnite, 262 son PackageFamilyName** (p. ej.
`MotionTwin.DeadCellsWin10_rtjy889c6zgtg`) — medido, no supuesto: los otros **86 son
`CONSOLE_<id>_<tipo>`** (Xbox 360, Xbox Arcade, aplicaciones), que no están en el catálogo de PC y
ningún endpoint de esta sección los devuelve. Para esos 86 no hay precio posible por esta vía y se
declaran como `unsupported` en vez de reintentarse. Para los 262, la deuda que `PLAN_LIBRARY.md` §5
dejó abierta sí queda resuelta: tienen identidad apta para precios, keyless y en MXN. Ya no hay motivo
para excluirlos por `state='subscription'`, salvo el semántico de "Game Pass no es posesión" (que
sigue siendo correcto como etiqueta, no como bloqueo de precio).

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
  (`9NKVX66J0ZSK`, medido). Un StoreId **sí** se tarifica, pero por otra ruta: `products/{StoreId}`
  (ver *Correcciones medidas después*).
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
| **`displaycatalog.mp.microsoft.com/v7.0/products/{StoreId}?market=MX&languages=es-mx`** | **200 sin key, con el precio**, aceptando mayúsculas (`9NKVX66J0ZSK`) y minúsculas (`9nkvx66j0zsk`), con `fieldsTemplate=Details` y sin él. **Este es el endpoint que hacía falta**: un StoreId suelto **sí** se puede tarificar, sin pasar por el `search` de título. El `alternateId=ProductId` que falla arriba es otra cosa: es el parámetro de `products/lookup`, no esta ruta |
| Sobre de ese response | `{"Product": {...}}` — objeto **singular**, no `Products[]` como `products/lookup`. El payload de dentro es el mismo (`ProductId`, `PreferredSkuId`, `LocalizedProperties[0].ProductTitle`, `DisplaySkuAvailabilities`), solo cambia el sobre. Un parser que lea uno solo se queda ciego en la mitad de los caminos |
| Un StoreId inexistente en `products/{id}` | **410** `ProductNotFound`. No es un fallo del proveedor: es una respuesta autoritativa, y reintentarla en cada carga solo re-pregunta algo ya contestado |

#### Los dos caminos de identidad, y por qué hacen falta los dos

| Camino | Cuándo aplica | Cobertura |
|---|---|---|
| PackageFamilyName → `products/lookup` | La fila de Xbox de `user_library` trae la PFN como `store_game_id` | **262 de los 348** juegos de Xbox medidos en el export. Los otros 86 son `CONSOLE_<id>_<tipo>` (Xbox 360 y aplicaciones): no están en el catálogo de PC y ningún endpoint los devuelve |
| StoreId → `products/{id}` | El juego tiene oferta de Microsoft en ITAD: su `deal_url` (redirector de ITAD, se sigue con el resolver de la Fase 1) termina en una URL de Microsoft y la última parte del camino es el StoreId | **Cualquier juego del catálogo**, tenga o no fila de Xbox en la biblioteca. En la medición que abrió esto: `OCTOPATH TRAVELER` (game_id 90) no tiene fila de Xbox — la que hay es la de `TRAVELER II` — y la tienda sí lo vende en MXN |

El pase dirigido por biblioteca (Fase 2) solo alcanza el primer camino, y por eso se le sumó el segundo: el comparador responde "cuánto cuesta este juego en cada tienda", no "cuánto cuesta lo que tengo en Xbox".

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
- Para Epic, el slug de la URL se compara por igualdad estricta contra **cualquiera** de los campos que
  pueden llevarlo, porque Epic no usa un solo vocabulario: `urlSlug` suele traer el slug legible
  (`octopath-traveler`) pero en otras fichas trae un hash (`1adfedf6847a4304a920ccc3fd4a7d0f`) y el
  legible queda en `offerMappings[].pageSlug` / `catalogNs.mappings[].pageSlug`, que es justo la forma
  que llevan las URLs. Mirar solo `urlSlug` pierde esas fichas sin decir nada.
- Los términos de búsqueda salen de las **palabras del slug**, no del título guardado: `steam_games.name`
  se pide con `cc=mx&l=spanish`, así que un juego con nombre traducido no lo encuentra la tienda. El
  sufijo hexadecimal del slug hay que quitarlo antes de buscar (`floppy knights 6a735a` → 0 elementos;
  `floppy knights` → el producto). Aun así la aceptación es la misma igualdad exacta: ampliar los
  términos solo recupera coincidencias exactas que antes se perdían, nunca introduce un precio ajeno.
- Para Microsoft, el StoreId sale en la ruta de la URL final y ya viene en la capitalización que ITAD
  acepta.
- El resultado se guarda en `game_external_ids` **una vez** y no se vuelve a resolver.

**Coste real de identidad: cero llamadas de precio extra.** El paso (2) ya ocurre en la ficha del
juego y `deal_url` ya es una columna poblada. El paso (3) es un `HEAD` con seguimiento de redirección,
una sola vez por juego y tienda, y ya se pudo ver que no necesita key.

**Cuándo hay búsqueda por título** (y solo entonces):

1. ITAD **respondió** (no degradado) y no devolvió ninguna oferta de esa tienda: el juego no está cubierto,
   es exclusivo, o es reciente. El buscador de la tienda es entonces el único camino que queda.
2. Un id de la tienda que ya está en `game_external_ids` siempre gana: el respaldo no se paga dos veces.

Y con dos guardas que no se relajan: igualdad exacta de título normalizado
(`StoreTitleMatcher`, §6 Fase 1) y el id resultante pasa por `GameIdentityClaimer` como cualquier otro. Es un
camino **de segunda**, no el principal.

**Lo que este camino no es:** una forma de adivinar. El id sale del buscador, pero solo se acepta el
resultado cuyo título sea el mismo producto; todo lo demás es no-match, y un no-match cuesta un precio que
no se muestra mientras un falso positivo escribe el precio de otro juego. La asimetría es lo que fija la
dureza del guard, no el gusto.

**Asimetría medida entre las dos tiendas**, que decide qué esperar de cada una:

| | Buscador de Epic | Buscador de Microsoft |
|---|---|---|
| Título localizado | **No lo conoce**: `searchStore("Caballeros Floppy")` → 0 elementos | **Sí**: `products/search` devuelve `Caballeros Floppy` como primer resultado |
| Qué acepta | Igualdad contra el slug que el resultado declare (`urlSlug`, `productSlug`, los dos mappings) | Igualdad de título + `isGame == true` |
| De dónde sale el precio | De la misma respuesta del buscador | De `products/{StoreId}`, el catálogo autoritativo (no del `price` del buscador) |

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
- **`IStorePriceProvider` tiene dos métodos, y el segundo es el respaldo.** `FindOfferAsync(title, externalId, ct)`
  sigue con id externo obligatorio; `FindOfferByTitleAsync(title, ct)` es el que no tiene id y existe desde que
  se midió un juego que la tienda vende y ITAD no enlaza (`Floppy Knights`: deals MX de 16 tiendas y ninguna
  Microsoft, mientras el buscador lo encontraba a MXN 141). Quien llama decide cuándo está permitido: con ITAD
  degradado **no** (`ItadIdentityIsTrustworthy`), porque el silencio de un proveedor caído no significa que la
  tienda no lo venda.
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

### Fase 2 — Microsoft / Xbox — **implementado**

- **Las tres deudas que la Fase 1 no podía alcanzar**: `GetOrCreateOffer` sigue colgando de
  `game.Offers` (anclado a Steam) y el escritor nuevo **no lo usa**: el pase escribe por
  `(game_id, region, source, offer_key)` (migración `2026-10-04_game_offers_steam_less.sql`),
  `GameMergeService` ya borra la colisión de `game_offers` antes de repuntar (patrón de favoritos) y
  `steam_game_id` es `NULL`-able.
- **`uq_game_offers` NO se borró**, en contra de lo que decía este plan. Sigue siendo el único dedupe de
  las ofertas ancladas a Steam y una fila con `steam_game_id` nulo no lo debilita (Postgres trata los
  `NULL` como distintos). Se borra el día que ningún escritor pueda dejar `game_id` nulo, porque es
  `game_id` nulo lo que deja a `uq_game_offers_canonical` sin cubrir la fila.
- **Dos caminos de identidad** (ver §4.2), porque uno solo no cubre lo que el comparador promete:
  - **`MicrosoftStoreClient`** acepta la PackageFamilyName (`products/lookup`) **y** el StoreId de 12
    caracteres (`products/{id}`), y el parser lee los dos sobres del response (`Products[]` y
    `Product`). Un id que no es ninguna de las dos formas **lanza**, en vez de mandar una consulta que
    respondería vacío (indistinguible de "no se vende aquí").
  - **Pase de biblioteca**, dirigido por las filas de Xbox de `user_library`: `POST
    /api/library/prices/sync` con `{ "limit": N }` (1..50, default 10). Ventana de refresco de 7 días
    leída de `observed_at`, una sola escritura al final. Respuesta de conteos:
    `pending | unsupported | updated | failed | rejected | remaining`. Cubre **262 de los 348**, y los
    86 `CONSOLE_*` se cuentan como `unsupported`: no hay catálogo de PC que los devuelva.
  - **Refresco en el detalle**, espejo exacto del de Epic: StoreId ya reclamado en
    `game_external_ids`, o leído del `deal_url` de ITAD shop 48 siguiendo un salto. Esto es lo que
    tarifica un juego que **no** está en la biblioteca de Xbox (el caso que abrió la pregunta:
    `OCTOPATH TRAVELER`, `game_id` 90, sin fila de Xbox y con precio real en la tienda). Un **410** del
    catálogo es una respuesta autoritativa (el producto no existe) y no se reintenta; solo el transporte
    y el 5xx degradan a "conservar el snapshot".
- `source='microsoft'`, `classification='official'`, `pricing_type='regional'`, MXN nativo, sin FX.
- Identidad: `('xbox', storeId_en_minúsculas)` vía `GameIdentityClaimer`. Si el id ya pertenece a otro
  juego canónico, la oferta **no se escribe** y se cuenta como `rejected`.
- Gate propio: `steam_games.microsoft_refreshed_at` (`SQL/migrations/2026-10-05_...`), como el de Epic.
  El pase de biblioteca usa el suyo (`game_offers.observed_at`), porque puede tarificar un juego que no
  tiene fila de `steam_games` donde guardar un timestamp.
- **La oferta lleva las dos anclas cuando el juego tiene fila de Steam**, y el detalle **adopta** por la
  clave canónica antes de insertar: una oferta escrita sin fila de Steam y luego anclada a una habría
  chocado con `uq_game_offers_canonical` (23505) al reescribirse desde la ficha.
- **Un solo `ApplyStoreRefresh(game, source, ...)`** para las dos tiendas directas: las fases difieren en
  cómo encuentran el id, nunca en lo que escriben. `ApplyEpicRefresh` se generalizó en vez de duplicarse.
- UI: grupo **Microsoft Store** en el detalle, junto al de Epic.
- Gotcha medido: un título incluido en suscripción llega como availability `Purchase` extra a `0.0`; la
  regla única es `Actions` contiene `Purchase` **y** `ListPrice > 0`.
- **Corrección al plan: la exclusión de `state='subscription'` NO se tocó.** El plan la justificaba con
  "precio visible en la biblioteca", y la biblioteca **no pinta precios**: el contrato los normaliza
  (`Deals.Web/app/library/_lib/library-contract.ts`) y ninguna vista los renderiza. Cambiar
  `LibraryPriceBindingService` habría sido trabajo invisible: contrato, tests y semántica de estado a
  cambio de nada. Sigue en pie como prerrequisito del día que la biblioteca pinte precios (o de la página
  canónica), junto con el agregado por `game_id`, que hoy agrupa por `steam_game_id` y por eso no ve una
  oferta sin fila de Steam.

### Fase 3 — limpieza de ITAD (reducida a casi nada por medición de código)

Medido en el código, no estimado: **de esta fase no queda trabajo real**, y dos de sus filas eran
innecesarias o falsas.

| Cambio propuesto | Veredicto |
|---|---|
| Marcar toda oferta de ITAD como `pricing_type='fx_estimate'` | **Ya ocurre, y por la vía correcta.** `ApplyPricing` deriva el tipo de la **moneda del payload**: `MXN` → `regional`, `USD` con tasa → `fx_estimate`, otra → `unconverted`. Fijar `fx_estimate` a mano para ITAD sería peor: si algún día el payload declara MXN, un valor hardcodeado lo etiquetaría de estimación cuando es un precio real. La producción confirma el derivado: `itad | 16 | fx_estimate | USD` |
| `LookupByShopAsync` | **No hace falta.** Ese método era para resolver identidad cross-store por API. La identidad ya se resuelve por el `deal_url` del shop en el payload de precios + el redirector (`ResolveDealUrlAsync`), que es exactamente la cadena que la Fase 1 dejó verificada y que la Fase 2 reusa para el shop 48. Un segundo camino para el mismo dato es deuda, no cobertura |
| `source='itad'` en toda oferta de ITAD | **Ya es así** por construcción: el escritor pasa `ItadSource` fijo |
| Mantener ITAD para Ubisoft, Humble, Battle.net y GOG | Ya es el comportamiento: `ITAD__OfficialShopIds` los incluye y ninguna fase directa los pisa |
| Quitar `61` (y `62`/`48`) de `ITAD__OfficialShopIds` | **Descartado.** No borra ninguna fila: cambia la `classification` de una tienda oficial a `keyshop` (falso) y encoge el `shops=` de la petición, o sea menos dato. Detalle en la tabla de abajo |

| Decisión de producto | Razón |
|---|---|
| **Las ofertas de ITAD de las tiendas que ya tienen cliente directo se conservan** (`61` Steam, `16` Epic, `48` Microsoft) | La fila de ITAD es el dato en **USD** del proveedor y se quiere para análisis; la fila directa es el precio **oficial en MXN**. Son dos hechos distintos y conviven, porque `uq_game_offers` es `(steam_game_id, source, offer_key)`: `itad/48` y `microsoft/store` no colisionan, igual que `itad/16` y `epic/store` ya conviven en la misma ficha. Lo que **no** puede pasar es que la estimada se lea como precio de la tienda ni compita como mejor precio: eso ya lo impide `pricingType === 'fx_estimate'` (nunca marca `better`) más el «≈» y la tasa en la celda. El trabajo era de etiquetado, **no de borrado** |

### Fase 4 — enlace de Ubisoft (alcance reducido a lo aprobado: **A**)

**Sin `games.publisher` y sin migración.** El enlace de búsqueda por título va en **toda** ficha, que era
la opción elegida: aparece también en juegos que Ubisoft no vende, y eso se aceptó a cambio de no depender
de una columna nueva ni de que ITAD devuelva una oferta del shop 62.

URL **medida**, no inventada (Akamai responde 302 a cualquier sonda, así que la forma había que comprobarla):

| Candidata | Resultado |
|---|---|
| `store.ubisoft.com/es-mx/search?q=…` | **302** → `store.ubisoft.com/ofertas/home` → `/home`. Inservible |
| `store.ubisoft.com/search?q=…` | **200**, redirige a `/ofertas/search?lang=es_MX&q=…` |
| **`store.ubisoft.com/ofertas/search?lang=es_MX&q=…`** | **200 sin redirección**, y devuelve el término tal cual (probado con `OCTOPATH%20TRAVELER%E2%84%A2`) |

El `™`/`®` se quita del término antes de codificarlo (ruido para el buscador de la tienda). El enlace es
una **búsqueda**, no la ficha del producto, y el texto visible lo dice: «Buscar en Ubisoft Store». Es un
`<a>` del navegador: el servidor nunca hace esa petición, así que la protección anti-bot de Ubisoft no
afecta.

Queda fuera de alcance, si algún día se quiere: `games.publisher`/`games.developer` con rellenado desde los
payloads de Epic y Microsoft, y el enlace condicionado al publisher.

### Fase 5 — job multi-tienda dirigido por la wishlist (medida en código; un hueco corregido, otro abierto)

**La pasada de precio ya existe, medida en código:** `WishlistSyncService.RefreshWishedGamesAsync`
llama `steamGameService.GetByAppIdAsync(appId, forceRefresh: false)`, que es **el mismo pipeline de la
ficha**. Ese pipeline ya incluye la fase de Epic (Fase 1) y la de Microsoft (Fase 2), cada una con su
ventana de 7 días y su propio timestamp. O sea que la wishlist **ya pide precios MX nativos** a las dos
tiendas, en background, con `ProviderRequestGovernor`, `MaxRefreshesPerHour` y pacing. No hay que cablear
un job nuevo. Lo que falta son dos huecos concretos:

| Hueco medido | Detalle |
|---|---|
| **El filtro de candidatos ignora los timestamps de tienda** | **Corregido.** `RefreshWishedGamesAsync` ahora suma `EpicRefreshedAt` y `MicrosoftRefreshedAt` a la condición, junto a `OffersRefreshedAt` y `GgDealsRefreshedAt`. Sin eso, un juego cuya llamada a tienda **falló** (degradada: su sello no avanza) pero cuyo ITAD salió bien no era candidato, y su precio de Epic/Microsoft no se reintentaba hasta que venciera la ventana de **otro** proveedor |
| **La identidad se descubre solo si hay una oferta que buscar** | La cadena de §4.5 saca el id del `deal_url` del shop en el payload de ITAD, que existe solo cuando ese shop vende el juego. Un juego sin oferta de Epic no obtiene identidad, y sin identidad nunca se pregunta. La pasada de identidad separada sigue teniendo sentido, pero **no bloquea nada hoy**: la ficha funciona por el camino de la Fase 2 |

Lo que sí sigue siendo cierto del plan original: **no se crea un scheduler nuevo.** El orden que evita
saturar (todos los títulos, después todos los precios) ya lo impone el pipeline por sí solo.

Presupuesto: con `ProviderRequestGovernor` (token bucket 1 req/s y burst 10) una wishlist de 600
juegos son ~20 min por pasada completa, en background y sin tocar el camino de request del usuario. El
gate por tienda hace que el caso normal sea casi todo cache-hit.

Regla de orden que evita saturar: **primero todos los títulos, después todos los precios.** Mezclados,
un juego con identidad sin resolver reintentaría la búsqueda en cada ciclo.

`PriceAlertScheduler` (`PLAN_WISHLIST.md` §7) consume el resultado después; con precios MX reales, la
alerta de Epic se vuelve útil por primera vez.

### Fase 6 — UI **implementada**

| Cambio | Estado |
|---|---|
| Badge por fila: **`Precio regional MX`** vs **`Estimado`** | Hecho en `PricingBadge` (`game-client.tsx`), en las dos formas de lista: la tabla por tienda (Epic/Microsoft/ITAD) y la lista agregada de gg.deals. `unconverted` no lleva badge: su celda de MXN ya dice «Sin conversión», y un segundo aviso del mismo hecho es ruido |
| Enlace propio por tienda, nunca el `itad.link` | Ya estaba para Epic y Microsoft; Microsoft pasó a usar la URL final de `xbox.com` cuando el StoreId vino de ahí |
| Enlace **Buscar en Ubisoft Store** | Hecho (Fase 4, opción A), junto a «Ver en Steam» |
| Wishlist: columna **Sincronización** | Hecho. Cinco badges, uno por proveedor, con la fecha corta o «—». Sustituye al badge «Identificado en ITAD», que se eliminó |
| Wishlist: `Mín. oficial` = menor entre **ITAD + Epic + Steam + Microsoft** | Hecho en `WishlistController`: el mínimo agrupado cubría ITAD/Epic/Microsoft (todos viven en `game_offers`) y **le faltaba Steam**, que no es una fila de ahí sino una columna del snapshot |
| «Mejor precio comparable»: **una tarjeta por proveedor, con el precio de ese proveedor** | Hecho. Antes el resumen decía quién ganaba cada comparación contra Steam y deduplicaba el ganador, así que el número de tarjetas cambiaba de un juego a otro sin que el lector pudiera saber por qué: un grupo donde Steam ganaba dejaba fuera el precio de su tienda. Ahora la marca «Más barato que Steam» dice lo que antes decidía qué se pintaba, y el conteo refleja qué tiendas tienen dato |
| Tarjeta **Bundle** cuando el juego viene dentro de uno | Hecho, con el precio del bundle si el proveedor lo publicó. No lleva la marca de «más barato que Steam» y no entra en ninguna comparación: el precio de un bundle no es el precio del juego |

### El resumen del detalle: qué decide cada tarjeta

Está escrito aquí porque el mismo fallo ocurrió dos veces por dos puertas distintas y las dos veces se vio
como una etiqueta mentirosa, no como un error.

| Pieza | Regla |
|---|---|
| Qué es una oferta comparable | `mxnCurrentPriceMinor != null` y `pricingType != 'unconverted'`. `regional` y `fx_estimate` sí; `unconverted` no |
| Qué tarjetas se pintan | Una por grupo **con** ofertas comparables: Epic, Microsoft, ITAD, gg.deals. Grupo sin ofertas → sin tarjeta |
| Qué precio muestra cada tarjeta | El de la oferta comparable **más barata de ese grupo**, en su moneda ya convertida a MXN por el backend. Nunca el de Steam |
| El precio directo de Steam | Es el titular de la ficha y la referencia de la marca; **no** es una tarjeta. `steamComparablePrice` exige `game.currency === MXN`, si no la comparación no tendría sentido |
| Verde y etiqueta en el resumen | Verde = el precio más bajo **de todas las tarjetas** (`lowestMxn`). La etiqueta dice contra qué gana: «Más barato que Steam» si le gana, «El más barato de las tiendas» si es el más bajo sin llegar a ganarle. Sin precio de Steam en MXN no se puede afirmar que le gane, así que la etiqueta es la segunda |
| Estimaciones y el puesto del más barato | Una `fx_estimate` **sí compite** por el verde. Un `≈` en verde es un precio convertido; excluirlo ponía el verde en una tarjeta más cara que otra de la misma pantalla (Floppy Knights), que se lee peor que la imprecisión de la conversión |
| Marca «Más barato que Steam» | Solo si el precio del grupo es menor. El `≈` y el badge de la fila ya dicen si la cifra es una conversión y de qué tipo de tienda sale |
| Empate dentro del grupo | Orden léxico por `shopName` y `offerKey`, para que la misma ficha elija siempre la misma fila |
| Tarjeta Bundle | El tier más barato **con precio** de todos los bundles del juego, prefiriendo MXN. Sin precio en ningún tier → sin tarjeta. `pickPricedBundleTier` en `app/games/[steamAppId]/_lib/bundle-card.ts`, con `bundle-card.test.ts` |
| Deduplicación | Ya no existe: cada tarjeta muestra el precio de su propio grupo, así que dos grupos no pueden pintar el mismo precio |

**Las dos formas del mismo error, para reconocerlas si vuelven:** heredar el precio de Steam en un grupo
**vacío** (la guarda de `length === 0`) y heredarlo en un grupo donde **Steam gana** la comparación. En los dos
casos el precio y el enlace eran de Steam y el encabezado de la tienda: se atribuía a una tienda un precio que
no es suyo. La regla que las cierra a las dos es que el precio y su etiqueta salgan siempre de la **misma**
fuente: el grupo del que se está hablando.

**Y una tercera, la del verde ausente:** mientras el verde significaba «le gana a Steam», una ficha donde Steam
gana —el caso frecuente, porque ITAD publica en USD y el precio regional mexicano de Steam suele ser más bajo—
no tenía ninguna tarjeta en verde y el resumen parecía roto. Medido con Absolum (appid 1904480): Steam
**282.99 MXN** contra los 18.74 USD (≈ 347 MXN) de la propia Microsoft Store en el enlace de ITAD. El verde
pasó a marcar el más bajo, y la etiqueta a decir contra qué gana.

**Y una cuarta, la del verde en la tarjeta equivocada:** en el primer intento de arreglar la tercera, el verde
solo miraba precios **regionales** para no presentar una conversión como mejor que un precio de la tienda. En
Floppy Knights (appid 1057800) el resultado fue el verde en **Epic a 71.99 MXN** mientras gg.deals mostraba
**16.56** e ITAD **20.69** — los dos estimados y los dos más bajos. Un verde sobre una tarjeta más cara que
otra de la misma pantalla se lee como que el resumen no sabe sumar. La regla que queda: **el verde es el número
más bajo de todos**, y de dónde sale cada cifra ya lo dicen el `≈` del precio y el badge de la fila.

Caso a comprobar en cada cambio de esta sección: una ficha con ofertas de los cuatro proveedores debe tener
**exactamente una** tarjeta en verde, y ese precio debe ser el menor de los cuatro. Fichas útiles por sus
diferencias: 1904480 (Steam gana), 1057800 (los más bajos son estimaciones), 599140 (sin Microsoft) y 2221920
(con Epic).

Nota sobre el badge y la moneda: el tono es `success` para `regional` y `muted` para `fx_estimate`, no
por decoración sino porque son afirmaciones distintas — un precio en pesos de la tienda y una conversión
propia. La tabla de ofertas ya estaba ordenada por precio; con precios MX reales, Epic aparece arriba solo.

### Orden obligatorio

| Paso | Estado |
|---|---|
| ~~1. Cerrar la Fase 1 en producción (§2.1)~~ | **hecho** (`05fff68`, `80b7e5f`, `865ec95`; precio Epic en MXN nativo en la ficha) |
| 2. Fase 2 (Xbox) | **hecho y verificado en producción** tras corregir `ItadMicrosoftShopId` a `48` (§8) |
| 3. Fase 3 (limpieza de ITAD) | **cerrada sin código**: el `fx_estimate`, el `source='itad'` y la cobertura de Ubisoft/Humble/Battle.net/GOG ya son el comportamiento actual; `LookupByShopAsync` no hace falta y quitar shops del `shops=` quedó descartado. Ver la sección para el detalle medido en código |
| 4. Fase 4 (enlace Ubisoft) | **hecho** con el alcance aprobado (opción A: enlace de búsqueda por título en toda ficha, sin `games.publisher` ni migración) |
| 5. Fase 5 (job dirigido por wishlist) | **hueco corregido** (el filtro de candidatos ya mira los sellos de Epic y Microsoft). Queda abierto, sin bloquear nada: la pasada de identidad separada |
| 6. Fase 6 (UI por tienda) | **hecho**: badge de procedencia, enlace propio por tienda, enlace de Ubisoft, columna de sincronización en la wishlist y `Mín. oficial` con Steam incluido |

El orden sigue siendo el mismo: la 5 depende de la cadena de identidad de la 1 y la 2.

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
| 2 | `MicrosoftStoreClient`, StoreId + PackageFamilyName, conversión decimal→menor | 6–8 h — **hecha** (los dos caminos de identidad) |
| 3 | Config + `LookupByShopAsync` + `pricing_type` | 2–3 h — **nada que hacer**, ver Fase 3 |
| 4 | Enlace Ubisoft por título (sin columnas de publisher) | 1 h — **hecha** |
| 5 | Filtro de candidatos del job + pasada de identidad | 1 h el hueco corregido; 4–6 h la pasada de identidad |
| 6 | Badges + enlaces + columna de sincronización + `Mín. oficial` con Steam | 3–4 h — **hecha** |

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
| Un falso positivo de título escribe el precio de otro juego | **La coincidencia por título no es el camino principal** (§4.5): es el respaldo, corre solo cuando ITAD respondió sin enlace, y el guard es igualdad exacta de título normalizado (`StoreTitleMatcher`). Medido con el caso que más se le parece: el buscador de Microsoft devuelve `Minecraft Dungeons II` primero para `Minecraft`, y el guard lo rechaza. Un desajuste no escribe |
| `region` mal poblada pisa snapshots MX con US | La clave única debe incluir `region` **antes** de escribir la primera oferta no-Steam |
| Microsoft devuelve SKUs `0.0` y de licencia | Regla explícita: `Purchase` en `Actions` y `ListPrice > 0` |
| Ubisoft: sin dato posible | Diferido y declarado, no simulado |
| El `™` y los sufijos de edición rompen el guard de título | Test de normalización con los valores reales medidos (`OCTOPATH TRAVELER™`, `OCTOPATH TRAVELER 0 Digital Deluxe Edition`) |
| **El título guardado está localizado y la tienda no conoce la traducción** — ocurrido: `Floppy Knights` (appid 1057800) se guarda como `Caballeros Floppy` porque `steam_games.name` se pide con `cc=mx&l=spanish`; `searchStore` con ese título devuelve **0 elementos** y la fase cerraba en no-match. Afectaba a **todo** juego con nombre traducido, no a un caso raro | Los términos de búsqueda salen de las **palabras del slug** (identidad que ya se tiene de ITAD, sin traducción posible), quitando el sufijo hexadecimal para que la búsqueda lo acepte; el título guardado queda como segundo intento. La aceptación no cambia, así que ampliar términos solo recupera coincidencias exactas. Corregido en `EpicStoreClient` con el caso medido en el comentario |
| **Epic publica el slug en más de un campo y no siempre coinciden** — ocurrido: el `urlSlug` de `Floppy Knights` es el hash `1adfedf6847a4304a920ccc3fd4a7d0f` y el slug legible (`floppy-knights-6a735a`, el que llevan las URLs y el que da ITAD) solo aparece en `offerMappings[].pageSlug` / `catalogNs.mappings[].pageSlug`. Comparar solo `urlSlug` perdía esas fichas en silencio | `CarriesSlug` compara el slug contra los cuatro campos, todos por igualdad exacta. En el control (`OCTOPATH TRAVELER™`) el camino viejo sigue siendo el que casa, así que no hay regresión |
| **Un juego que ITAD no liste en la shop 48 no tenía precio de Microsoft, aunque la tienda lo venda** — medido: `Floppy Knights` tiene deals MX de 16 shops (incluida `16` Epic y `61` Steam) y **ninguna** es Microsoft; su StoreId (`9nlm4tthcwsh`, **MXN 141**) solo se podía sacar del buscador de la tienda | **Resuelto** con el respaldo por título en las dos tiendas (§4.5). Ojo con la asimetría medida: el catálogo de Microsoft **sí** está localizado (la búsqueda con el título en español devuelve coincidencia exacta y primer resultado), a diferencia de la de Epic, que con un título traducido devuelve 0 elementos |
| **La tarjeta de «mejor precio comparable» titulaba con el grupo, no con el precio** — ocurrido: con las ofertas de Microsoft más caras que Steam, la tarjeta decía `Microsoft Store` sobre el precio **y el enlace de Steam** (etiqueta `Steam · precio directo`). El mismo síntoma de la vez anterior por otra puerta: antes era un grupo **vacío**, ahora es un grupo donde **Steam gana la comparación** | El encabezado es `best.label` (de quién es el precio), no `group.heading` (de qué grupo se comparó). Un grupo donde Steam gana muestra una tarjeta de Steam, y la deduplicación por ganador ya evitaba pintarla dos veces. Si algún día se quiere saber *contra qué* se comparó, es una línea aparte y debe salir del grupo que de verdad ganó el turno, no del primero que la generó |
| **El buscador de la tienda no indexa todo su catálogo** — medido: `Graveyard Keeper` (appid 599140) es comprable en MX a **MXN 249** (`products/c11gzgmkrtcv`, SKU con acción `Purchase`) y el buscador devuelve 17 resultados **sin él**, con ni una variante de query (`Graveyard Keeper`, `... Xbox`, `graveyard-keeper`, `en-US`) que lo saque | Sin camino: ITAD no tiene deal de la shop `48` en ningún país (US, DE, GB, BR probados) y el buscador no lo indexa, así que no hay id que descubrir. El precio de esa tienda queda ausente y **no se inventa**. Es un límite del proveedor: ITAD rastrea 407 ofertas de Microsoft en total (`docs/PLAN_ITAD.md` §3.0) |
| El esquema de las URLs de `itad.link` cambia y la cadena exacta deja de parsear | El id extraído se valida contra la tienda antes de persistirlo (una llamada por juego, una vez). Si la URL final no encaja con el patrón conocido, la identidad queda sin resolver en vez de escribir un id dudoso. El respaldo por título sigue disponible
| La cadena de `itad.link` termina en un host distinto según el cliente | Medido: para un cliente sin navegador termina en `www.epicgames.com/store/p/<slug>`, no en `store.epicgames.com/<locale>/p/<slug>`. El parser se ancla en el segmento `/p/` y acepta cualquier subdominio de `epicgames.com`, rechazando hosts que solo lo imitan (`store.epicgames.com.evil.example`). Verificado con 14 formas de URL |
| Cloudflare bloquea la **página** de la tienda (403) aunque la redirección ya diera la URL | El resolvedor no juzga el estado final: la cadena de redirecciones es la información, el documento final no. Un 403 en el último salto es lo esperado y no impide extraer el slug. Solo un fallo de transporte degrada a "conservar el snapshot" |
| Microsoft devuelve precios no numéricos para títulos incluidos en suscripción | Medido: AC Mirage base se anuncia como `displayPrice: "Incluido"` con `price` nulo. Regla: sin precio numérico no se escribe oferta |
| La detección por publisher se convierte en un motor de reglas | Es un dato de ficha y un enlace saliente. Ninguna decisión de precio depende de `publisher` |
| El job de identidad reintenta la búsqueda de título en cada ciclo | Regla de orden de la Fase 5: identidad primero, precios después, y el mapeo se persiste |
| **Una fusión de juegos borra la oferta colisionante del superviviente** (§6 Fase 2: `game_offers` es único por `(game_id, region, source, offer_key)`, así que el repunte no puede conservar las dos) | Es una fila de precio, no identidad: el gate de 7 días la reescribe en el siguiente pase. `game_merges` **no** la cuenta (solo registra `moved_external_ids`, `moved_steam_games`, `moved_library_rows`); si hace falta trazar esa pérdida, la columna que falta es `dropped_game_offers` |
| El pase de precios de Xbox no tiene botón ni ruta BFF | Es mantenimiento de admin: se corre con la llamada documentada en §10. `covers/sync` tiene botón porque su resultado **se ve** en la grilla; una biblioteca que no pinta precios no justifica UI. Si la biblioteca pinta precios (o llega la página canónica), el botón es el mismo molde |
| **Un id de proveedor mal copiado falla en silencio** — ocurrido: `ItadMicrosoftShopId` quedó en `62`, que es **Ubisoft Store**, no Microsoft | El síntoma fue una fase que **corrió, selló su timestamp y no escribió precio**, y se diagnosticó como "el binario es viejo": tres rondas por un dato que estaba en la doc del propio repo y se podía comprobar en una llamada. Mitigaciones aplicadas: el mapeo verificado (`16` Epic, `36` GreenManGaming, `48` Microsoft, `61` Steam, `62` Ubisoft) y el comando de comprobación viven en el comentario de las constantes de `SteamGameService`; la regla quedó en `AGENTS.md` → *Diagnóstico de un proveedor externo*. El guard de host de `ExtractStoreId` evitó que el id equivocado escribiera un precio de otro juego: con un juego que Ubisoft sí vende, la URL final habría sido de `ubisoft.com` y el parser la rechaza |
| **"La fase selló su timestamp" se confunde con "el binario no se desplegó"** — ocurrido, y es lo que costó las rondas | Cada fase directa tiene su propio timestamp (`epic_refreshed_at`, `microsoft_refreshed_at`); el pase de biblioteca usa `game_offers.observed_at`. **Timestamp nuevo + 0 filas de esa tienda = la fase corrió y no encontró identidad.** Antes de proponer un rebuild, pedir esa consulta. Segundo efecto del mismo diseño: un no-match también sella la ventana, así que un ciclo fallido se tarda 7 días en reintentarse — el botón *Actualizar ofertas* (`POST /api/steam/games/{appId}/refresh`, `forceRefresh=true`) es lo que lo salta |

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
| 1 | **Nombre traducido:** `Floppy Knights` (appid 1057800) tiene su ficha de Steam **`Caballeros Floppy`** y el slug de ITAD `floppy-knights-6a735a`. Con las palabras del slug, `searchStore` devuelve el elemento cuyo `offerMappings[].pageSlug` **es** el slug de ITAD y **MXN 71.99**; con el título guardado devuelve **0 elementos**. Medido, y es la corrección que metió el slug como términos de búsqueda |
| 1 | Respaldo por título **medido con el código real contra las tiendas en vivo** (10/10): Epic por slug `floppy-knights-6a735a` → 7199 MXN y `immortals-fenyx-rising` → **7990 MXN** (el buscador devuelve `Immortals Fenyx Rising Edición Standard` con `urlSlug` basura pero `offerMappings[].pageSlug` correcto); Epic con el título **localizado** `Caballeros Floppy` → null; control `octopath-traveler` → 16199/53999 MXN; Microsoft por título `Caballeros Floppy` → `9nlm4tthcwsh` 14100 MXN; Microsoft con `Minecraft` → **null**; y los dos límites medidos: `Graveyard Keeper` no lo encuentra su propio buscador (null) aunque `products/c11gzgmkrtcv` dé 24900 MXN |
| 1 | `SQL/checks/game_offers_anchor.sql` devuelve 0 filas, incluida la consulta 6 (ninguna fila de Epic con FX) — **verificado en producción** |
| 2 | Dead Cells desde el export de Playnite: `('xbox', 9nkvx66j0zsk)` resuelto y oferta **MXN 439.00** regional |
| 2 | `POST /api/library/prices/sync` devuelve `updated > 0`, `rejected = 0`, y una segunda corrida seguida devuelve `pending = 0` (la ventana de refresco ya cubre lo escrito) |
| 2 | Un juego que está en Xbox y en Steam muestra su oferta `source='microsoft'` en la ficha, dentro del grupo **Microsoft Store**, en MXN nativo |
| 2 | **Caso que destapó el hueco de cobertura:** `Floppy Knights` (appid 1057800), que Steam sirve en MX como **`Caballeros Floppy`**. ITAD **sí** lo conoce (`lookup/id/shop/61/v1` con la clave `app/1057800` → uuid `018d937f-3e47-…`; con el id pelado → `null`, que es como una sonda mal escrita se lee como "no existe"), y sus deals MX son de 16 shops: **`16` Epic, `61` Steam**, ninguna `48`. Epic lo vende a **MXN 71.99** (`searchStore` con las palabras del slug) y Microsoft a **MXN 141.00** (`products/9nlm4tthcwsh`, SKU `0010` con `Purchase`, parser → `14100`). Los dos precios salían invisibles por motivos distintos: Epic por el `urlSlug`-hash y el título traducido, Microsoft porque ITAD no anunció su shop |
| 3 | Las filas de ITAD de las tiendas que ya tienen cliente directo **se conservan** (§6, Fase 3): `itad/61`, `itad/16` y `itad/48` conviven con `steam` directo, `epic/store` y `microsoft/store`. Lo que se comprueba no es su ausencia sino su etiqueta: toda fila de ITAD con payload USD lleva `pricing_type='fx_estimate'`, y ninguna fila directa (`source` ∉ `itad`) lleva `fx_estimate`. **Corregido:** esta fila pedía «ninguna fila con `source='itad'` y `shop_id='61'`», que es exactamente lo contrario de la decisión de §6 y habría hecho fallar la comprobación sobre un sistema correcto |
| 4 | La ficha muestra el enlace **Buscar en Ubisoft Store** en todos los juegos, construido por título (`game.name`, sin `™`/`®`), apuntando a `https://store.ubisoft.com/ofertas/search?lang=es_MX&q=…`. **Corregido:** esta fila pedía comprobar `games.publisher`, que la Fase 4 no creó — el alcance aprobado fue la opción A, sin columna ni migración, así que la prueba era imposible por diseño |
| 5 | Correr el job dos veces: la segunda no emite ninguna resolución de identidad (todo cache-hit en `game_external_ids`) y solo refresca los precios con el gate vencido |
| 5 | La wishlist de 600 juegos produce ofertas Epic y Microsoft en MXN, con `pricing_type='regional'` |
| 6 | La ficha muestra Epic arriba con 161.99 y badge "Precio regional MX" |

### Fase 2: despliegue y verificación — **ejecutado y verificado en producción**

Estado: las dos migraciones están aplicadas, la API corre con el runtime `azurelinux3.0` y el camino por
StoreId está confirmado sobre la tienda viva (`Caballeros Floppy` → `9nlm4tthcwsh`, MXN 141.00). Se conserva
el procedimiento porque es el molde de cualquier fase directa futura —incluida la de arreglos como el de los
slugs de Epic—, no como trabajo pendiente.

Requisito previo: las seis variables `Microsoft__*` en el `.env` del servidor **antes** de reconstruir.
Compose pasa cadena vacía a una variable sin definir, y la validación de `MicrosoftOptions` rechaza el
arranque (mismo footgun que tuvo Epic en la Fase 1).

```bash
# 1. Migraciones a mano: no hay runner.
docker exec -i <postgres> psql -U <usuario> -d <bd> \
  < SQL/migrations/2026-10-04_game_offers_steam_less.sql
docker exec -i <postgres> psql -U <usuario> -d <bd> \
  < SQL/migrations/2026-10-05_steam_games_microsoft_refreshed_at.sql

# 2. Reconstruir la API (compose lee las variables al crear el contenedor).
docker compose build api frontend && docker compose up -d api frontend

# 3. Pase acotado. Repetir hasta que `remaining` sea 0. El controlador es AdminWithId.
curl -s -X POST "$API/api/library/prices/sync" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"limit": 10}'

# 4. El camino por StoreId no necesita pase: se resuelve al abrir la ficha del juego.
#    Verificación mínima: abrir la ficha y comprobar grupo «Microsoft Store» + la fila en game_offers.
```

Lectura, sin modificar nada:

```sql
-- La oferta de Xbox de un juego que también está en Steam (Dead Cells: app 588650).
SELECT o.game_id, o.steam_game_id, o.source, o.offer_key, o.region, o.classification,
       o.pricing_type, o.original_currency, o.original_regular_price_minor,
       o.original_current_price_minor, o.mxn_current_price_minor, o.discount_percent,
       o.deal_url, o.observed_at
FROM steam_games g
JOIN game_offers o ON o.game_id = g.game_id AND o.source = 'microsoft'
WHERE g.app_id = 588650 AND g.region = 'mx';

-- La identidad reclamada: el StoreId en minúsculas (§4.2).
SELECT game_id, namespace, external_id FROM game_external_ids
WHERE namespace = 'xbox' ORDER BY game_id LIMIT 20;

-- Cobertura: cuántas filas de Xbox de la biblioteca siguen sin precio.
SELECT count(*) FILTER (WHERE o.game_offer_id IS NULL) AS sin_precio, count(*) AS total
FROM user_library l
LEFT JOIN game_offers o ON o.game_id = l.game_id AND o.source = 'microsoft' AND o.region = 'mx'
WHERE l.store = 'xbox' AND l.game_id IS NOT NULL;
```

Esperado: Dead Cells `43900` con `pricing_type='regional'` y `steam_game_id` **no** nulo (también está en
Steam); OCTOPATH `41970` sobre `139900` (−70 %).

Añadido al cerrar la Fase 2 con el camino por StoreId (misma consulta 1, con dos filas esperadas si el
juego está en la biblioteca de Xbox **y** tiene oferta de ITAD):

```sql
-- El camino que abrió esto: OCTOPATH TRAVELER (921570 / game_id 90) no tiene fila de Xbox, y la tienda
-- sí lo vende. La fila directa sale del deal_url de ITAD shop 48, no de user_library.
SELECT g.app_id, o.source, o.game_id, o.steam_game_id, o.pricing_type, o.original_currency,
       o.original_regular_price_minor, o.original_current_price_minor, o.discount_percent,
       o.deal_url, o.observed_at, g.microsoft_refreshed_at
FROM steam_games g
JOIN game_offers o ON o.game_id = g.game_id AND o.source = 'microsoft'
WHERE g.app_id = 921570 AND g.region = 'mx';

-- La identidad que reclamó el camino nuevo.
SELECT game_id, namespace, external_id FROM game_external_ids
WHERE namespace = 'xbox' ORDER BY updated_at DESC LIMIT 10;
```

Esperado: `Microsoft Store`, `regional`, `MXN`, `original_current_price_minor = 41970`,
`original_regular_price_minor = 139900`, `discount_percent = 70`,
`deal_url = https://apps.microsoft.com/detail/9n9606cc950j` y `microsoft_refreshed_at` no nulo.

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
  -d '{"query":"query{Catalog{searchStore(keywords:\"Octopath Traveler\",country:\"MX\",locale:\"es-MX\",count:5){elements{title urlSlug productSlug id offerType offerMappings{pageSlug} catalogNs{mappings{pageSlug}} price(country:\"MX\"){totalPrice{discountPrice originalPrice currencyCode}}}}}}"}'
# Los cuatro campos del slug son necesarios: `urlSlug` puede ser un hash. Ojo también con los términos:
# el título guardado está en español (`cc=mx&l=spanish`), así que hay que buscar las palabras del slug.
```

```bash
UA='DealsExt/1.0 (+https://amitzi.xyz)'

# Microsoft MX por PackageFamilyName (el GameId de Playnite)
curl -s "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&value=MotionTwin.DeadCellsWin10_rtjy889c6zgtg&fieldsTemplate=Details&market=MX&languages=es-mx"

# Epic MX por oferta exacta. `id` es el de `searchStore.elements[].id` (el *offer* id) y `namespace` el
# sandboxId; el `productId` de `StorePageMapping` es **otro** identificador y aquí da 404.
curl -s -A "$UA" -X POST https://store.epicgames.com/graphql -H 'Content-Type: application/json' \
  -d '{"query":"query{Catalog{catalogOffer(namespace:\"1c4745021d4243a783b459d928b9088f\",id:\"f51ebf66eb42420bba6cdd7d87cf56af\",locale:\"es-MX\"){id title urlSlug price(country:\"MX\"){totalPrice{discountPrice originalPrice currencyCode}}}}}"}'
# Esperado (control): OCTOPATH TRAVELER™ 16199 / 53999 MXN. Si esta sonda no responde eso, la sonda está
# mal y no la app: con `productId` en vez del offer id devuelve 404 sobre un juego que sí existe.

# Epic: del slug a los identificadores duros (resuelve también cuando la búsqueda no encuentra nada)
curl -s -A "$UA" -X POST https://store.epicgames.com/graphql -H 'Content-Type: application/json' \
  -d '{"query":"{ StorePageMapping { mapping(pageSlug: \"floppy-knights-6a735a\") { pageSlug productId sandboxId } } }"}'
# floppy-knights-6a735a -> sandboxId f7ca5706875344d8a1fe695093353fae, que es el `epic_game_id` de la URL

# Cadena de identidad exacta: seguir el deal_url de ITAD hasta el slug.
# Ojo: el último salto responde 403 (Cloudflare sobre la página de la tienda). La URL final sirve igual:
# el resolver no mira el estado, solo la cadena de redirecciones.
curl -sL -o /dev/null -w '%{url_effective}\n' "https://itad.link/018d959e-e79a-7189-a071-bc4f582ff378/?app=wrzdeb"

# Microsoft MX por título. La lista viene en **`productsList`**: buscar las claves `Products`/`products`
# devuelve 0 resultados sin error, que es la peor forma de fallar. El catálogo de Microsoft **sí** está
# localizado, así que aquí el título en español funciona (a diferencia de Epic).
curl -s "https://apps.microsoft.com/api/products/search?query=caballeros%20floppy&hl=es-MX&gl=MX"

# ITAD: identidad cross-store, sin key. **La clave no es el id pelado**: por tienda es la forma que esa
# tienda usa, y para Steam es `app/<appid>`. Con `["1057800"]` el proveedor contesta `{"1057800":null}`
# sobre un juego que sí tiene, que es exactamente cómo una sonda mal escrita se lee como "no existe".
curl -s -X POST https://api.isthereanydeal.com/lookup/id/shop/61/v1 -H 'Content-Type: application/json' -d '["app/1057800"]'
# Esperado (control): {"app\/1057800":"018d937f-3e47-71b8-8361-d514b297c93e"}  (floppy-knights)
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
