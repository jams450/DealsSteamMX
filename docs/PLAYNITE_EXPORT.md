# DealExt: exportar biblioteca de Playnite

Estado: procedimiento ejecutado. El export real está en `docs/dealext-library.json` (ignorarlo en Git: es una biblioteca personal).

Objetivo: crear un JSON que permita fijar el contrato de import de DealExt. El archivo debe incluir por
juego el id de tienda, el plugin/proveedor, el título, los nombres de plataforma que Playnite conozca, si
está instalado y la fecha de alta.

> Este procedimiento es para **Playnite 10 estable** (10.55 verificado). No actualizar a Playnite 11:
> sigue en alpha y elimina las extensiones de scripts PowerShell de Playnite 10.

## 1. Resultado que debes traer a Linux

Guarda el archivo como `dealext-library.json` en una ubicación fácil de copiar (USB, partición compartida,
cloud privado, etc.).

El archivo **no contiene credenciales**, pero contiene títulos de tu biblioteca y rutas opcionales: no lo
publiques ni lo subas a un repositorio público.

Ejemplo mínimo de forma esperada:

```json
[
  {
    "GameId": "1091500",
    "PluginId": "cb91dfc9-b977-43bf-8e70-55f46e410fab",
    "Source": "Steam",
    "Name": "Cyberpunk 2077",
    "Platforms": ["PC (Microsoft Windows)"],
    "IsInstalled": true,
    "Added": "2026-01-01T00:00:00"
  }
]
```

No edites, normalices ni borres campos del export. `GameId` y `PluginId` son los campos críticos.

El script de §2.3 escribe `Platforms` con los **nombres** de plataforma que Playnite tenga en cada juego. Es
**metadato**: solo alimenta las sugerencias del diálogo de importación de consolas. Nunca decide posesión ni
identidad, y su ausencia (o un solo valor serializado como texto) no rompe nada: la fila pide la plataforma
en vez de sugerirla.

Dos clases de filas conviven en el export:

| Fila | `Source` | Puerta de import |
|---|---|---|
| Compra de tienda | nombre de tienda (`"Steam"`, `"Epic"`, …) | `POST /api/library/import` (la importación normal) |
| Consola / manual (sin plugin de tienda) | `null` | `POST /api/library/console-import/preview` → `…/commit` (`PLAN_CONSOLE.md` §7) |

Las consolas **no** llevan un `Source` inventado: la fila conserva `Source: null`, que es exactamente lo que
exige el contrato de consola. Si le pones el nombre de la consola en `Source`, va a la importación de tiendas
y la rechaza entera.

## 2. Método recomendado: extensión PowerShell propia

No requiere instalar ningún add-on. Playnite 10 admite extensiones de script PowerShell de forma nativa.

### 2.1 Crear la carpeta

Con Playnite cerrado, abre PowerShell y ejecuta **una** de estas opciones:

```powershell
# Instalación normal de Playnite
New-Item -ItemType Directory -Force "$env:APPDATA\Playnite\Extensions\DealExtExporter"
```

```powershell
# Instalación portable: sustituye <RUTA_PLAYNITE> por la carpeta donde está Playnite.exe
New-Item -ItemType Directory -Force "<RUTA_PLAYNITE>\Extensions\DealExtExporter"
```

### 2.2 Crear `extension.yaml`

Dentro de `DealExtExporter`, crea `extension.yaml` con este contenido exacto:

```yaml
Id: DealExtLibraryExporter
Name: DealExt Library Exporter
Author: user
Version: 1.0.0
Module: ExportLibrary.psm1
Type: Script
```

### 2.3 Crear `ExportLibrary.psm1`

En la misma carpeta crea `ExportLibrary.psm1` con este contenido exacto:

```powershell
function Export-LibraryJson()
{
    param($scriptMainMenuItemActionArgs)

    $path = $PlayniteApi.Dialogs.SaveFile("JSON|*.json")
    if (-not $path) { return }

    $games = foreach ($game in $PlayniteApi.Database.Games)
    {
        # `Platforms` son NOMBRES y solo alimentan las sugerencias del import de consolas. Se escribe
        # siempre un arreglo (vacío si el juego no tiene plataformas): nunca un error, nunca `Source`
        # inventado — una fila de consola conserva `Source` nulo, que es lo que su contrato exige.
        $platformNames = @()
        if ($game.Platforms)
        {
            $platformNames = @($game.Platforms | Where-Object { $_.Name } | ForEach-Object { $_.Name })
        }

        [pscustomobject]@{
            GameId      = $game.GameId
            PluginId    = $game.PluginId.ToString()
            Source      = if ($game.Source) { $game.Source.Name } else { $null }
            Name        = $game.Name
            Platforms   = $platformNames
            IsInstalled = $game.IsInstalled
            Added       = $game.Added
        }
    }

    $games | ConvertTo-Json -Depth 3 | Out-File -FilePath $path -Encoding utf8
    $PlayniteApi.Dialogs.ShowMessage("Exported $($games.Count) games to $path")
}

function GetMainMenuItems()
{
    param($getMainMenuItemsArgs)

    $menuItem = New-Object Playnite.SDK.Plugins.ScriptMainMenuItem
    $menuItem.Description = "DealExt: Export Library JSON"
    $menuItem.FunctionName = "Export-LibraryJson"
    $menuItem.MenuSection = "@"
    return $menuItem
}
```

> `$PlayniteApi` es una variable global inyectada por Playnite. No añadas `param($PlayniteApi)` al
> script: esa forma no es el contrato correcto de una script extension de Playnite 10.

> `Platforms` viaja como nombres y **nunca sustituye a `Source`**: una fila de consola deja `Source` en
> `null` y entra por la importación de consolas (`PLAN_CONSOLE.md` §7). Si un juego tiene una sola
> plataforma y tu PowerShell la serializa como texto en vez de arreglo, el import lo lee como «sin
> sugerencia» y pide la plataforma: degrada a preguntar, nunca a un slug equivocado.

### 2.4 Exportar

1. Abre Playnite.
2. Si ya estaba abierto cuando creaste los archivos: **Tools → Reload Scripts**. Si no aparece, reinicia
   Playnite.
3. Abre el menú **Extensions**.
4. Elige **`DealExt: Export Library JSON`**.
5. Guarda el archivo como `dealext-library.json`.
6. Confirma el mensaje con el número de juegos exportados.

## 3. Verificación antes de volver a Linux

Abre `dealext-library.json` con Bloc de notas. Comprueba lo siguiente:

1. El primer carácter no vacío es `[` y el último es `]`.
2. Hay al menos un juego de Steam y contiene:
   - `GameId` con un appid numérico, por ejemplo `"1091500"`.
   - `PluginId` con un GUID.
   - `Source` igual a `"Steam"` o al nombre real que muestre tu Playnite.
3. Hay al menos una fila por cada fuente que uses: Amazon, Xbox/Game Pass, GOG, Epic, Ubisoft, Battle.net,
   etc.
4. **No borres** filas de Xbox/Game Pass: se importarán como biblioteca con tag `Game Pass`, sin precio ni
   alerta.
5. Las filas de tienda traen `"Platforms"` con al menos un nombre (el script nuevo siempre escribe el
   campo; puede llegar vacío `[]` en juegos sin plataforma, y eso es válido).
6. Si tu Playnite tiene colección de consola, busca filas con `"Source": null`: son las que entran por el
   diálogo «Importar consolas». **No** las edites para ponerles un nombre de tienda en `Source`: eso las
   manda a la importación de tiendas, que exige `Source` no nulo y rechaza el archivo entero.

Si el archivo comienza con `{` en vez de `[`, sigue siendo JSON válido, pero no lo modifiques: tráelo tal
cual y se ajusta el import al formato real.

## 4. Resultado real del export

`docs/dealext-library.json` valida el contrato: raíz de arreglo, **2586** juegos y ningún campo crítico
vacío. Fuentes confirmadas: Steam 1168, Epic 394, Xbox 348, GOG 342, Amazon 195, Ubisoft Connect 89,
Humble 39 y Battle.net 11.

El mapa `Source`/`PluginId` ya quedó fijado en `PLAN_LIBRARY.md` §6. `Added` llega como
`/Date(<epoch-milisegundos>)/`; el import debe aceptarlo junto con ISO.

Medido: el archivo real (`dealext-library.json`, 2026-09-18) se generó con el script anterior: **0** filas
traen `Platforms` y **0** tienen `Source: null`, así que todavía **no** ejercita el camino de consolas. Para
usar el diálogo «Importar consolas» hay que reexportar con el script de §2.3 (que ya escribe `Platforms` y
conserva `Source: null` en las filas sin plugin de tienda) y comprobar la lista de §3.

No hacen falta capturas, cuentas, cookies, tokens, rutas de instalación ni credenciales.

## 5. Qué hará DealExt con el export

| Entrada | Resultado previsto |
|---|---|
| Steam (`GameId` = appid) | Se liga al comparador de precios directamente |
| Amazon / Prime Gaming | Nunca consulta ITAD; solo 33/195 coinciden por título con Steam. Esas muestran `Precio vinculado por título`; las demás quedan sin precio |
| Epic, GOG, Ubisoft Connect, Humble, Battle.net | Entran en biblioteca; se ligan a precio solo por identidad exacta o una única coincidencia de título |
| Xbox | Las 348 filas se importan como `state='subscription'`, tag `Game Pass`, sin precio, alerta ni badge "ya lo tienes" |
| Consola (`Source: null`) | No entran por aquí: van al diálogo «Importar consolas» (`POST /api/library/console-import/preview` → `commit`), que escribe la plataforma elegida y la identidad confirmada una por una. Sin precio, como cualquier fila de consola |
| Sin identidad o sin coincidencia | Se conserva en biblioteca con la nota `Sin precios vinculados`; nunca se inventa un precio |

El detalle del camino de consola —flujo, invariantes y límites— vive en `PLAN_CONSOLE.md` §7.

## 6. Fallback A: Library Exporter Advanced (CSV)

Úsalo si Playnite no carga el script o la política de Windows bloquea scripts.

1. En Playnite: **Add-ons → Browse**.
2. Busca e instala **`Library Exporter Advanced`** de `darklinkpower`.
3. En las opciones del add-on, selecciona como mínimo estas columnas:
   `GameId`, `PluginId`, `Source`, `Name`, `Platforms`, `IsInstalled`, `Added`.
4. Exporta CSV y llámalo `dealext-library.csv`.
5. Trae el CSV sin abrirlo ni re-guardarlo desde Excel; Excel puede alterar fechas, ids y codificación.

Página del add-on:
https://playnite.link/addons.html#LibraryExporter_54bf64c6-c453-4cbc-92f8-4960b56f930e

## 7. Fallback B: copiar la base de datos local

Solo si los dos métodos anteriores fallan.

1. **Cierra Playnite por completo**: los archivos se abren en modo exclusivo.
2. Copia estas dos carpetas/archivos:

| Instalación | Juegos | Fuentes |
|---|---|---|
| Normal | `%AppData%\Playnite\library\games\games.db` | `%AppData%\Playnite\library\sources\sources.db` |
| Portable | `<RUTA_PLAYNITE>\library\games\games.db` | `<RUTA_PLAYNITE>\library\sources\sources.db` |

3. Copia también los `extension.yaml` de los plugins de biblioteca relevantes:
   `%AppData%\Playnite\Extensions\*\extension.yaml` (o `<RUTA_PLAYNITE>\Extensions\*\extension.yaml` en
   instalación portable).
4. Trae esos archivos a Linux **sin editarlos**.

Son bases LiteDB 4 (formato de archivo v7). No son un archivo `library.db` único. El lector Linux todavía
no está implementado en DealExt, por eso este es el último recurso.

## 8. No usar

| Opción | Motivo |
|---|---|
| `NicodeSS/playnite-game-data-exporter` | Su JSON no incluye `GameId` ni `PluginId`; no sirve para ligar tiendas/precios |
| `zachvlat/playnite-json` | Solo resuelve el id de Steam y no exporta el `GameId`/`PluginId` general |
| Exportador integrado `LibraryExporterPS_Builtin` | CSV sin id de proveedor; insuficiente para el import |
| Playnite 11 alpha | Rompe compatibilidad con plugins/script extensions de Playnite 10 |

## 9. Referencias verificadas

- Playnite scripting: https://api.playnite.link/docs/tutorials/extensions/scripting.html
- Manifest de extensiones: https://api.playnite.link/docs/tutorials/extensions/extensionsManifest.html
- Ruta de biblioteca: https://api.playnite.link/docs/manual/gettingStarted/helpAndTroubleshooting/faq.html
- Library Exporter Advanced: https://playnite.link/addons.html#LibraryExporter_54bf64c6-c453-4cbc-92f8-4960b56f930e
- Código de Library Exporter Advanced: https://github.com/darklinkpower/PlayniteExtensionsCollection
