import { parseApiError } from "@/lib/bff/client-session";
import {
  CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT,
  normalizeCatalogFreshnessPage,
  type CatalogFreshnessFilter,
  type CatalogFreshnessPage
} from "@/lib/contracts/catalog-freshness";

// Una sola llamada por filtro y página sobre la lista de deseos del usuario autenticado: el sobre trae la
// lista seleccionada y los cuatro contadores, así que cambiar de filtro no dispara cuatro pedidos. La señal
// permite abortar el pedido anterior cuando el usuario cambia de filtro o de página antes de que llegue la
// respuesta. El usuario no viaja en la query: el backend lo deriva del token.
export async function getCatalogFreshnessPage(
  filter: CatalogFreshnessFilter,
  page = 1,
  pageSize = CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT,
  signal?: AbortSignal
): Promise<CatalogFreshnessPage> {
  const params = new URLSearchParams({ status: filter, page: String(page), pageSize: String(pageSize) });
  const response = await fetch(`/api/bff/discover/freshness?${params.toString()}`, { cache: "no-store", signal });
  if (!response.ok) {
    throw await parseApiError(response, "No se pudo cargar el estado de actualización de precios de tu lista de deseos");
  }
  // El filtro pedido es la única fuente de la etiqueta: una respuesta atrasada no puede renombrar la pestaña.
  return normalizeCatalogFreshnessPage(await response.json(), filter);
}
