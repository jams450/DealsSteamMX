import { parseApiError } from "@/lib/bff/client-session";
import { normalizeSteamGame, normalizeSteamSearch, type SteamGame, type SteamSearchResult } from "@/lib/contracts/steam";

export async function searchSteam(query: string): Promise<readonly SteamSearchResult[]> {
  const response = await fetch(`/api/bff/steam/search?name=${encodeURIComponent(query)}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron buscar juegos");
  return normalizeSteamSearch(await response.json());
}

export async function getSteamGame(appId: number): Promise<SteamGame> {
  const response = await fetch(`/api/bff/steam/games/${appId}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el juego");

  const game = normalizeSteamGame(await response.json());
  if (game === null) throw new Error("Steam devolvió una respuesta inválida");
  return game;
}
