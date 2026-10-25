import { parseApiError } from "@/lib/bff/client-session";
import {
  DISCOVER_PAGE_SIZE_DEFAULT,
  normalizeDiscoverPage,
  type DiscoverCategory,
  type DiscoverPage
} from "@/lib/contracts/discover";

// Una sola llamada por pestaña y página: el sobre trae la lista seleccionada y los tres contadores, así que
// cambiar de pestaña no dispara tres pedidos. La señal permite abortar el pedido anterior cuando el usuario
// cambia de pestaña o de página antes de que llegue la respuesta.
export async function getDiscoverPage(
  category: DiscoverCategory,
  page = 1,
  pageSize = DISCOVER_PAGE_SIZE_DEFAULT,
  signal?: AbortSignal
): Promise<DiscoverPage> {
  const params = new URLSearchParams({ category, page: String(page), pageSize: String(pageSize) });
  const response = await fetch(`/api/bff/discover?${params.toString()}`, { cache: "no-store", signal });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el descubrimiento");
  // La categoría pedida es la única fuente de la etiqueta: una respuesta atrasada no puede renombrar la
  // pestaña activa.
  return normalizeDiscoverPage(await response.json(), category);
}
