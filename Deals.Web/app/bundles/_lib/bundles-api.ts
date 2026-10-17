import { parseApiError } from "@/lib/bff/client-session";
import { normalizeBundleCatalog, type BundleCatalog } from "@/lib/contracts/bundles";

export async function getBundleCatalog(): Promise<BundleCatalog> {
  const response = await fetch("/api/bff/bundles", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el catálogo de bundles");
  return normalizeBundleCatalog(await response.json());
}
