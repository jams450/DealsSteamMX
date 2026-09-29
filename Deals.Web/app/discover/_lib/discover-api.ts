import { parseApiError } from "@/lib/bff/client-session";
import { normalizeSteamDiscover, type SteamDiscoverItem } from "@/lib/contracts/steam";

export type DiscoverList = "discount" | "historic" | "recent";

export async function getDiscover(list: DiscoverList): Promise<readonly SteamDiscoverItem[]> {
  const response = await fetch(`/api/bff/steam/discover?list=${list}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el descubrimiento");
  return normalizeSteamDiscover(await response.json());
}
