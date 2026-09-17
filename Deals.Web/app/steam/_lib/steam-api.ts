import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import { normalizeSteamGame, normalizeSteamSearch, type SteamGame, type SteamSearchResult } from "@/lib/contracts/steam";

export async function searchSteam(query: string): Promise<readonly SteamSearchResult[]> {
  const response = await fetch(`/api/bff/steam/search?name=${encodeURIComponent(query)}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron buscar juegos");
  return normalizeSteamSearch(await response.json());
}

export async function suggestGames(query: string, signal?: AbortSignal): Promise<readonly SteamSearchResult[]> {
  const response = await fetch(`/api/bff/steam/suggestions?query=${encodeURIComponent(query)}`, { cache: "no-store", signal });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar las sugerencias");
  return normalizeSteamSearch(await response.json());
}

export async function getSteamGame(appId: number, options?: { refresh?: boolean }): Promise<SteamGame> {
  if (options?.refresh === true) return refreshSteamGame(appId);

  const response = await fetch(`/api/bff/steam/games/${appId}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el juego");

  const game = normalizeSteamGame(await response.json());
  if (game === null) throw new Error("Steam devolvió una respuesta inválida");
  return game;
}

export async function refreshSteamGame(appId: number): Promise<SteamGame> {
  const response = await csrfFetch(`/api/bff/steam/games/${appId}`, { method: "POST", cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo actualizar el juego");

  const game = normalizeSteamGame(await response.json());
  if (game === null) throw new Error("Steam devolvió una respuesta inválida");
  return game;
}
