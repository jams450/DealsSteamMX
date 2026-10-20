## Guía rápida de colores (tema UI)

### Archivo central
- `app/globals.css`

Aquí están las variables globales y fondos principales.

### Variables clave para cambiar colores
En `:root` (modo claro) y `.dark` (modo oscuro):

- `--tabler-page-bg` → color base del fondo general de la app.
- `--tabler-sidebar-bg` → fondo del menú lateral (sidebar).
- `--tabler-surface-1`, `--tabler-surface-2`, `--tabler-surface-3` → tarjetas/paneles/superficies.
- `--tabler-border`, `--tabler-border-strong` → bordes.
- `--tabler-text`, `--tabler-text-soft` → texto principal y secundario.
- `--tabler-primary`, `--tabler-primary-hover` → color primario de acentos.

### Fondo con gradiente general
En el mismo archivo, bloques:
- `body { background: ... }`
- `.dark body { background: ... }`

Ahí se controla el gradiente global (radial/colores/opacidades).

### Menú lateral (hover de opciones)
Archivo:
- `components/navigation/app-navigation.tsx` → `AppNavigation()`

Ese renderer es el único que dibuja las rutas: lo consumen el riel de escritorio
(`ShellRail`) y el drawer móvil (`ShellDrawer`), ambos en
`components/navigation/shell-chrome.tsx`. La superficie del riel es la clase de componente
`.app-rail` (más `.app-rail-scroll` para la zona con scroll propio) en `app/globals.css`.

Clases reales (tokens semánticos, no utilidades de paleta):
- opción normal: `border-l-transparent text-secondary hover:border-l-[color:var(--color-accent)] hover:bg-[var(--color-accent-soft)] hover:text-primary`
- opción activa: `border-l-[color:var(--color-accent)] bg-[var(--color-accent-soft)] text-primary` (`aria-current="page"`)
- foco: `focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]`

Para ajustar el hover, cambia esas clases o el token `--color-accent-soft` en `app/globals.css`.

### Capa semántica (usar en componentes)
Los componentes consumen `--color-*`, no `--tabler-*` directo:

- `--color-page-bg`, `--color-surface-1/2/3`, `--color-border` / `--color-border-strong`
- `--color-text-primary` / `-secondary` / `-muted`, `--color-border-focus`
- `--color-accent` / `-hover` / `-soft` / `-contrast`
- `--color-success`, `--color-warning`, `--color-danger`, `--color-info`

Paletas alternativas en claro: `[data-theme="blue"]` y `[data-theme="light-blue"]`
(solo aplican fuera de `.dark`). En oscuro manda el bloque `.dark`.

Deuda pendiente: `:root` (sin `.dark`) declara `color-scheme: light` pero
`--tabler-page-bg: #0b1220`; el default dark de `app/layout.tsx` evita el choque.

### Nota importante
La app inicia en dark por defecto (ver `app/layout.tsx`, script de tema).
Si cambias solo `:root` y no `.dark`, visualmente puede parecer que no aplicó.
