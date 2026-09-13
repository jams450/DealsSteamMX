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
- `components/navigation/admin-shell.tsx`

Clases relevantes de hover:
- opción normal sidebar: `hover:border-blue-300/60 hover:bg-blue-400/20 hover:text-blue-100`
- opción activa sidebar: `border-[#0F3158] bg-[#0F3158] text-white`

Si quieres ajustar tono o intensidad del hover, cambia esas clases Tailwind.

### Nota importante
La app inicia en dark por defecto (ver `app/layout.tsx`, script de tema).
Si cambias solo `:root` y no `.dark`, visualmente puede parecer que no aplicó.
