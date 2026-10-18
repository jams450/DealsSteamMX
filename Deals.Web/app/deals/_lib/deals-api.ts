import { parseApiError } from "@/lib/bff/client-session";
import {
  GLOBAL_DEALS_PAGE_SIZE_DEFAULT,
  normalizeGlobalDealCatalog,
  type GlobalDealCatalog
} from "@/lib/contracts/global-deals";

export async function getGlobalDeals(
  page = 1,
  pageSize = GLOBAL_DEALS_PAGE_SIZE_DEFAULT
): Promise<GlobalDealCatalog> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  const response = await fetch(`/api/bff/deals?${params.toString()}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar las ofertas");
  return normalizeGlobalDealCatalog(await response.json());
}
