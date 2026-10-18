import { parseApiError } from "@/lib/bff/client-session";
import {
  BUNDLE_PAGE_SIZE_DEFAULT,
  normalizeBundleCatalog,
  type BundleCatalog
} from "@/lib/contracts/bundles";

export async function getBundleCatalog(
  page = 1,
  pageSize = BUNDLE_PAGE_SIZE_DEFAULT
): Promise<BundleCatalog> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  const response = await fetch(`/api/bff/bundles?${params.toString()}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el catálogo de bundles");
  return normalizeBundleCatalog(await response.json());
}
