import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  MAX_NAV_GROUP_ITEMS,
  appNavItems,
  isRouteActive,
  navGroups,
  nextFocusIndex
} from "../components/navigation/nav-config.ts";

const read = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

// --- Contrato de la navegación agrupada (riel izquierdo) ---------------------

test("cada ruta vive exactamente una vez y los grupos no están saturados", () => {
  const hrefs = navGroups.flatMap((group) => group.items.map((item) => item.href));
  assert.equal(new Set(hrefs).size, hrefs.length, "una ruta repetida monta dos enlaces al mismo destino");
  assert.deepEqual(hrefs, appNavItems.map((item) => item.href), "la lista plana debe ser el aplanado de los grupos");
  for (const group of navGroups) {
    assert.ok(group.label.trim().length > 0, "todo grupo necesita un nombre visible");
    assert.ok(group.items.length > 0, `el grupo «${group.label}» no puede estar vacío`);
    assert.ok(
      group.items.length <= MAX_NAV_GROUP_ITEMS,
      `el grupo «${group.label}» satura el riel con ${group.items.length} enlaces`
    );
  }
  assert.equal(new Set(navGroups.map((group) => group.label)).size, navGroups.length, "dos grupos con el mismo nombre se leen como uno");
});

test("todo destino del riel se declara activo sobre sí mismo y ninguna ruta activa dos enlaces", () => {
  for (const item of appNavItems) {
    assert.equal(isRouteActive(item.href, item.href), true, `${item.href} no se marca activo sobre sí mismo`);
    const active = appNavItems.filter((other) => isRouteActive(item.href, other.href));
    assert.deepEqual(active.map((other) => other.href), [item.href], `${item.href} activa más de un enlace`);
  }
});

test("una ruta anidada con entrada propia no activa también a su padre", () => {
  assert.equal(isRouteActive("/library/duplicates", "/library/duplicates"), true);
  assert.equal(isRouteActive("/library/duplicates", "/library"), false);
  assert.equal(isRouteActive("/library/reconciliation", "/library"), false);
  assert.equal(isRouteActive("/library/tiers", "/library"), false);
  assert.equal(isRouteActive("/wishlist/algo", "/wishlist"), true);
  assert.equal(isRouteActive("/discover/x", "/discover"), true);
  assert.equal(isRouteActive("/search", "/"), false);
});

// PRESERVE: mudar la barra al riel no puede perder ni renombrar un destino.
test("la mudanza conserva los doce destinos y sus etiquetas en el mismo orden", () => {
  assert.deepEqual(appNavItems.map((item) => item.href), [
    "/",
    "/search",
    "/discover",
    "/deals",
    "/wishlist",
    "/bundles",
    "/library",
    "/library/tiers",
    "/library/duplicates",
    "/library/reconciliation",
    "/users",
    "/jobs"
  ]);
  assert.deepEqual(appNavItems.map((item) => item.label), [
    "Inicio",
    "Comparar",
    "Descubrir",
    "Ofertas globales",
    "Wishlist",
    "Bundles",
    "Biblioteca",
    "Tier list",
    "Duplicados",
    "Reconciliación",
    "Usuarios",
    "Jobs"
  ]);
});

// PRESERVE: la API pública de los shells usada por cada página sigue igual.
test("ProductShell y AdminShell conservan sus props", async () => {
  const product = await read("components/navigation/product-shell.tsx");
  for (const prop of ["title: string", "subtitle?: string", "meta?: ReactNode", "children: ReactNode"]) {
    assert.ok(product.includes(prop), `ProductShell dejó de aceptar \`${prop}\``);
  }
  const admin = await read("components/navigation/admin-shell.tsx");
  for (const prop of ["username: string", "section: string", "title: string", "actions?: ReactNode", "children: ReactNode"]) {
    assert.ok(admin.includes(prop), `AdminShell dejó de aceptar \`${prop}\``);
  }
});

// --- Trampa de foco del drawer ----------------------------------------------

test("la trampa de foco envuelve en los bordes y no interviene dentro de la lista", () => {
  assert.equal(nextFocusIndex(4, 5, false), 0, "Tab en el último elemento debe volver al primero");
  assert.equal(nextFocusIndex(0, 5, true), 4, "Shift+Tab en el primero debe ir al último");
  assert.equal(nextFocusIndex(2, 5, false), null, "en el medio, el navegador decide");
  assert.equal(nextFocusIndex(2, 5, true), null, "en el medio, el navegador decide");
  assert.equal(nextFocusIndex(-1, 5, false), 0, "foco fuera de la lista entra por el principio");
  assert.equal(nextFocusIndex(-1, 5, true), 4, "foco fuera de la lista entra por el final");
  assert.equal(nextFocusIndex(0, 0, false), null, "lista vacía: nada que atrapar");
  assert.equal(nextFocusIndex(0, -1, true), null, "total inválido: nada que atrapar");
});

// --- Contrato de los shells (riel + drawer) ---------------------------------

test("el chrome compartido concentra el drawer accesible y el riel", async () => {
  const chrome = await read("components/navigation/shell-chrome.tsx");
  assert.match(chrome, /export function ShellRail/, "el riel debe vivir en el chrome compartido");
  assert.match(chrome, /export function ShellDrawer/, "el drawer debe vivir en el chrome compartido");
  assert.match(chrome, /role="dialog"/);
  assert.match(chrome, /aria-modal="true"/);
  assert.match(chrome, /event\.key === "Escape"/, "Escape debe cerrar el drawer");
  assert.match(chrome, /nextFocusIndex\(/, "el trap debe usar la matemática compartida y probada");
  assert.match(chrome, /querySelectorAll/, "el trap necesita la lista de enfocables reales");
  assert.match(chrome, /style\.overflow = "hidden"/, "el drawer bloquea el scroll del body");
  assert.match(chrome, /triggerRef\.current\?\.focus\(\)/, "al cerrar debe devolver el foco al disparador");
  assert.match(chrome, /usePathname\(\)/, "cerrar al cambiar de ruta exige observar el pathname");
  assert.match(chrome, /matchMedia/, "un drawer abierto no debe sobrevivir al salto a escritorio");
  assert.match(chrome, /onClick=\{onClose\}/, "el backdrop debe cerrar por click");
});

test("los dos shells usan el mismo riel y no montan una navegación propia", async () => {
  for (const file of ["components/navigation/product-shell.tsx", "components/navigation/admin-shell.tsx"]) {
    const source = await read(file);
    assert.match(source, /from "\.\/shell-chrome"/, `${file} debe consumir el chrome compartido`);
    assert.match(source, /<ShellRail/, `${file} debe montar el riel izquierdo`);
    assert.match(source, /<ShellDrawer/, `${file} debe montar el drawer accesible`);
    assert.match(source, /<AppNavigation/, `${file} debe dibujar los grupos de rutas`);
    assert.doesNotMatch(source, /<nav[\s>]/, `${file} no puede declarar su propia navegación: habría dos landmarks`);
    assert.match(source, /min-w-0/, `${file} necesita que la columna de contenido no desborde`);
  }
});

test("el header queda delgado: sin navegación horizontal y con el trigger solo en móvil", async () => {
  const chrome = await read("components/navigation/shell-chrome.tsx");
  assert.match(chrome, /export function ShellHeader/, "el header compartido contiene título, meta y controles");
  assert.doesNotMatch(chrome, /<nav[\s>]/, "el header no es un contenedor de navegación");
  assert.match(chrome, /lg:hidden/, "el disparador del menú solo existe en móvil");
});

// Verificado en Chromium (fixture local): el riel es display:none hasta 1024px y el drawer
// cerrado no monta ningún landmark de navegación, así que nunca hay dos navegaciones expuestas.
test("el riel solo existe desde 1024px y el drawer cerrado no monta navegación", async () => {
  const css = await read("app/globals.css");
  assert.match(css, /\.app-rail \{[^}]*display: none/s, "el riel debe estar oculto por defecto");
  assert.match(css, /@media \(min-width: 1024px\) \{\s*\.app-rail \{\s*display: flex/s, "el riel debe aparecer en 1024px");
  assert.match(css, /\.app-rail-scroll \{[^}]*overflow-y: auto/s, "la zona de rutas necesita scroll propio");
  const chrome = await read("components/navigation/shell-chrome.tsx");
  assert.match(chrome, /if \(!open\) return null;/, "el drawer cerrado debe desmontarse, no quedar oculto en el DOM");
});

test("el shell de admin conserva sesión, tema y cierre de sesión", async () => {
  const admin = await read("components/navigation/admin-shell.tsx");
  assert.match(admin, /\/api\/auth\/logout/, "el cierre de sesión no puede perderse en el rediseño");
  assert.match(admin, /ThemeToggle/, "el cambio de tema sigue disponible");
  assert.match(admin, /username/, "la sesión activa sigue visible");
});
