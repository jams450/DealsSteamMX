# DealExt: exportar la biblioteca de Heroic (Epic/GOG)

Estado: MVP implementado en `scripts/export-heroic-library.mjs`. Es **export local + subida manual**: no
hay inicio de sesión de tiendas en vivo, ni subida automática, ni sincronización. La exportación se
genera en tu máquina y tú la subes en DealExt.

Objetivo: obtener un JSON con la biblioteca que Heroic ya tiene en disco —Epic y GOG—, con la identidad
de cada juego intacta, y subirlo a mano por el mismo camino de importación que ya usa Playnite. Playnite
sigue funcionando y no se toca.

> **Requisito previo:** Node.js **22 o superior** en la máquina donde corre el script. Es el mínimo que
> exige este entregable. Comprueba con `node --version`. El script usa solo módulos internos de Node
> (`node:fs`, `node:path`, `node:test`): no hay `npm install`, ni dependencias, ni red.

## 1. Refresca Heroic antes de exportar

Los dos archivos que lee este script son **cachés**, no una consulta a las tiendas: Heroic los reescribe
solo cuando refresca esa biblioteca. Si exportas un caché de hace un mes, subirás una foto de hace un
mes con aspecto de actual.

1. Abre Heroic.
2. Inicia sesión en **Epic** y en **GOG** (solo si aún no lo hiciste en ese equipo).
3. Entra en **Biblioteca** y usa **Actualizar / Refrescar biblioteca** en cada tienda.
4. Espera a que la lista termine de cargar; cierra Heroic si quieres, pero no hace falta.
5. Ejecuta el exportador (sección 3).

El script también avisa: imprime la edad del caché de cada tienda y, si supera los **7 días**, escribe
una advertencia en `stderr` con ese número de días. No hay TTL en estos cachés: la advertencia es la
única señal de que la foto puede estar vieja.

## 2. Dónde está la carpeta de configuración de Heroic

`--dir` apunta a la carpeta que **contiene** `store_cache/` (no a `store_cache`). Según cómo instalaste
Heroic:

| Sistema | Instalación | Ruta esperada de `--dir` |
|---|---|---|
| Windows | Normal (instalador) | `%APPDATA%\heroic` (p. ej. `C:\Users\tú\AppData\Roaming\heroic`) |
| Linux | Nativa (AppImage/deb/rpm) | `~/.config/heroic` |
| Linux | Flatpak | `~/.var/app/com.heroicgameslauncher.hgl/config/heroic` |
| macOS | Normal | `~/Library/Application Support/heroic` |

Para confirmar la ruta en tu equipo, busca la carpeta `store_cache` dentro de esas ubicaciones:

```bash
ls ~/.config/heroic/store_cache
ls ~/.var/app/com.heroicgameslauncher.hgl/config/heroic/store_cache
```

```powershell
Get-ChildItem "$env:APPDATA\heroic\store_cache"
```

Si un listado falla, esa no es tu ruta; prueba la siguiente. La carpeta correcta muestra
`gog_library.json` y/o `legendary_library.json`.

## 3. Comando

Linux / macOS:

```bash
node scripts/export-heroic-library.mjs --dir "$HOME/.config/heroic"
```

Windows (PowerShell), instalación normal:

```powershell
node scripts/export-heroic-library.mjs --dir "$env:APPDATA\heroic"
```

Windows, ruta con espacios (Flatpak no existe en Windows; esto cubre nombres de usuario o discos con
espacios): cita siempre la ruta con `"`.

El resultado por defecto es `dealext-heroic-library.json` en el directorio actual.

## 4. Opciones

| Opción | Efecto |
|---|---|
| `--dir <ruta>` | **Obligatoria.** Carpeta de configuración de Heroic (la que contiene `store_cache/`). |
| `--stores <lista>` | Tiendas separadas por coma: `epic,gog`. Por defecto exporta **las dos**. |
| `--output <ruta>` | Archivo de salida. Por defecto `dealext-heroic-library.json` en el directorio actual. Debe quedar **fuera** de la carpeta de configuración de Heroic. |
| `--force` | Sobrescribe `--output` si ya existe. Sin este flag, un archivo existente es un error. Nunca autoriza escribir dentro de la configuración de Heroic. |
| `-h`, `--help` | Muestra la ayuda y sale con 0. |

Ejemplo: solo GOG, a una ruta concreta:

```bash
node scripts/export-heroic-library.mjs --dir "$HOME/.config/heroic" --stores gog --output ~/dealext-gog.json
```

Si una tienda seleccionada no tiene caché, el script **falla** con un mensaje accionable en vez de
exportar media biblioteca: refresca esa tienda en Heroic o sácala de `--stores`.

## 5. Qué lee y qué no lee

Lee dos archivos, siempre por nombre fijo, dentro de `<dir>/store_cache/`:

| Tienda | Archivo | Lista | `runner` esperado |
|---|---|---|---|
| Epic | `legendary_library.json` | `.library` | `legendary` |
| GOG | `gog_library.json` | `.games` | `gog` |

Del `GameInfo` de Heroic solo se conservan cuatro campos: `store`, `storeGameId` (el `app_name` exacto),
`title` y `isInstalled` (el booleano `is_installed`). **Nunca** salen del script `install.install_path`,
`folder_name`, `save_path`, `art_*` ni ningún otro campo.

Tampoco abre archivos de sesión: `legendary/user.json`, `gog_store/auth.json`, tokens de GOG o cualquier
otra credencial quedan fuera. El script no hace red, no pide contraseñas y no inicia sesión en ninguna
tienda. Y por la misma razón **no escribe nada dentro de esa carpeta**: ni siquiera con `--force`, un
`--output` que caiga en la configuración de Heroic se rechaza (ver §7).

## 6. Formato de salida

```json
{
  "format": "dealext-library",
  "version": 1,
  "source": "heroic",
  "exportedAt": "2026-10-18T12:34:56.789Z",
  "games": [
    { "store": "epic", "storeGameId": "Ginger", "title": "Fortnite", "isInstalled": true },
    { "store": "gog", "storeGameId": "1207658924", "title": "The Witcher 3", "isInstalled": false }
  ]
}
```

- `exportedAt` es ISO-8601 en UTC, generado al escribir el archivo.
- `storeGameId` es el `app_name` de Heroic **sin normalizar**: para Epic es el appName que usa Epic y para
  GOG es el id numérico del producto (hasta 64 dígitos, el mismo tope por campo que aplica el import).
- El estado de posesión lo fuerza el servidor al importar; en el archivo solo viaja `isInstalled`.
- Orden determinista: primero `epic`, después `gog`; dentro de cada tienda, por `storeGameId` ascendente
  (comparación por unidad de código, no por locale). Dos ejecuciones con el mismo caché producen el mismo
  orden.
- Duplicados de `store`+`storeGameId`: se conserva la **primera** fila y el resto se cuenta en `stderr`.
- No hay fecha de alta (`AddedAt`) en este sobre: el import conserva la que ya tenga el juego.

## 7. Límites y fallos cerrados

| Situación | Resultado |
|---|---|
| `--dir` ausente o desconocida, `--stores` con tienda no soportada, argumento desconocido | sale con **2** y muestra la ayuda; no escribe nada |
| Falta el caché de una tienda seleccionada | sale con **1**, dice qué archivo falta y cómo refrescarlo |
| Caché con JSON inválido o sin la lista esperada | sale con **1**; nunca escribe una salida parcial |
| Fila sin `is_installed` booleano, sin `app_name`, con título vacío, con `runner` ajeno o con id GOG no numérico | sale con **1** indicando archivo y número de fila |
| `app_name` de más de **64** caracteres o título de más de **256** | sale con **1**: son los mismos topes por campo que aplica el import |
| Id de GOG no numérico | sale con **1**; el id de GOG es el número de producto (hasta 64 dígitos, la cota del import) |
| Total de juegos 0 | sale con **1**: no se escribe un archivo vacío que parecería una biblioteca completa |
| Más de **10 000** juegos | sale con **1** (es el tope del import de DealExt) |
| JSON de salida de más de **10 MiB** | sale con **1** (mismo tope que el import) |
| `--output` apunta a un caché de entrada | sale con **1** y no toca el caché |
| `--output` cae **dentro de la carpeta de configuración de Heroic** —incluidos `store_cache/`, `gog_store/` y `legendary/`, y también cuando se llega ahí por un symlink— | sale con **1** y no escribe nada: un export no puede pisar cachés ni credenciales, ni con `--force` |
| `--output` ya existe y no se pasó `--force` | sale con **1**; el archivo previo queda intacto |

`stdout` queda **vacío** en una ejecución normal: los conteos, avisos y el recordatorio van a `stderr`, y
el JSON solo al archivo (`--help` sí usa `stdout`).

Salida típica de `stderr`:

```
[heroic-export] epic: 394 juegos, 12 instalados (caché /home/tú/.config/heroic/store_cache/legendary_library.json)
[heroic-export] gog: 342 juegos, 3 instalados (caché /home/tú/.config/heroic/store_cache/gog_library.json)
[heroic-export] duplicados omitidos: 1 (se conserva la primera fila de cada tienda+id)
[heroic-export] recordatorio: esto refleja el último refresco de Heroic, no es una consulta en vivo y no certifica tu posesión actual. Revisa los conteos antes de subir el archivo.
[heroic-export] escrito /home/tú/dealext-heroic-library.json (91234 bytes, 736 juegos)
```

## 8. Revisar el archivo antes de subirlo

Con Node (no hace falta `jq`):

```bash
node -e "const e=require('./dealext-heroic-library.json');console.log(e.format,e.version,e.source,e.games.length)"
```

Comprueba a mano:

1. `format` es `dealext-library`, `version` es `1` y `source` es `heroic`.
2. Hay filas `epic` **y** `gog` si esperabas las dos tiendas.
3. El número de juegos se parece al que muestra Heroic. Si es mucho menor, el caché estaba viejo o la
   cuenta no había terminado de refrescar: vuelve al paso 1.
4. No aparece ninguna ruta de instalación ni token en el archivo.

## 9. Subir a DealExt

Es una subida **manual**, con la sesión de administrador ya abierta en DealExt:

1. Entra en la sección de importación de biblioteca.
2. Elige el archivo `dealext-heroic-library.json`.
3. Confirma la subida. El servidor rechaza el archivo entero si el sobre, la tienda o alguna fila no
   cumplen el contrato; no escribe nada a medias.
4. Revisa el resumen de la importación (altas y actualizaciones). La importación no borra juegos que no
   vengan en el archivo.

La importación usa el mismo camino que el import de Playnite: sesión, CSRF y límites de 10 MiB y 10 000
filas. El sobre de la sección 6 viaja a `POST /api/library/provider-export`; el estado de posesión y la
identidad canónica los decide el servidor, no el archivo.

## 10. Verificado y no verificado

Verificado leyendo el código fuente de Heroic (v2.15.2 y v2.22.4):

- Las rutas `store_cache/gog_library.json` y `store_cache/legendary_library.json`, creadas por
  `CacheStore` con `cwd: 'store_cache'`.
- Las claves `.games` y `.library` y los campos `runner` (`gog` / `legendary`), `app_name`, `title`,
  `is_installed`.
- La marca `__timestamp.<clave>` con la fecha del último refresco.
- Que la biblioteca se guarda con `max_value_lifespan = null`, es decir **sin TTL**: el archivo puede ser
  arbitrariamente viejo.
- El id de Flatpak `com.heroicgameslauncher.hgl` y el nombre de app `heroic` del `package.json`.

Derivado (no ejecutado en una instalación real de este equipo):

- Las rutas de la tabla de la sección 2: salen de `app.getPath('userData')` de Electron con
  `name: "heroic"`. La ruta de Flatpak es la convención `~/.var/app/<id>/config/`.
- Los conteos del ejemplo de `stderr` son ilustrativos.

No verificado:

- Una corrida real del script contra una instalación viva de Heroic en Windows y en Linux. El script se
  probó con configuraciones sintéticas (`node --test scripts/export-heroic-library.test.mjs`), no con
  datos personales.
- Que la versión de Heroic de tu equipo escriba el caché justo después de refrescar. El código lo hace;
  la corrida real queda pendiente.
- La pantalla de subida de DealExt (`POST /api/library/provider-export`): su implementación va en el
  trabajo paralelo de la app, no en este entregable.

## 11. Referencias primarias

Código de Heroic Games Launcher (rama principal y etiquetas v2.15.2 / v2.22.4):

- `src/backend/cache.ts` — `cwd: 'store_cache'`, `__timestamp.<clave>`, sin TTL cuando
  `max_value_lifespan` es `null`.
- `src/backend/storeManagers/gog/electronStores.ts` — `new CacheStore<GameInfo[], 'games'>('gog_library', null)`.
- `src/backend/storeManagers/legendary/electronStores.ts` — `new CacheStore<GameInfo[], 'library'>('legendary_library', null)`.
- `src/common/types.ts` — `interface GameInfo` (`runner`, `app_name`, `title`, `is_installed`,
  `install`, `folder_name`, `art_*`).

Comandos usados para verificar cada constante (el resultado esperado va al lado):

```bash
# Forma de los cachés y su carpeta. Esperado: 'gog_library' y cwd 'store_cache'.
curl -sS https://raw.githubusercontent.com/Heroic-Games-Launcher/HeroicGamesLauncher/v2.22.4/src/backend/storeManagers/gog/electronStores.ts | grep -n "libraryStore"
curl -sS https://raw.githubusercontent.com/Heroic-Games-Launcher/HeroicGamesLauncher/v2.22.4/src/backend/cache.ts | grep -n "cwd"

# Sin TTL para la biblioteca. Esperado: el segundo argumento es null.
curl -sS https://raw.githubusercontent.com/Heroic-Games-Launcher/HeroicGamesLauncher/v2.22.4/src/backend/storeManagers/legendary/electronStores.ts | grep -n "libraryStore" -A 3

# Id de Flatpak. Esperado: com.heroicgameslauncher.hgl.
curl -sS https://flathub.org/api/v2/appstream/com.heroicgameslauncher.hgl | grep -o com.heroicgameslauncher.hgl
```

## Componente interno de GOG

Heroic incluye `gog-redist` (Galaxy Common Redistributables) en el caché de GOG.
El exportador lo omite explícitamente: es un componente del launcher, no un juego.
Los demás identificadores de GOG siguen exigiendo dígitos; no se omiten filas inválidas arbitrariamente.
Por eso el número de juegos exportados puede ser uno menor que el número de entradas del caché.
Fuente: [Heroic GOGLibraryManager.refresh](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/main/src/backend/storeManagers/gog/library.ts).
