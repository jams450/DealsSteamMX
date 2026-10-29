import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeGameTierYears } from "@/lib/contracts/game-tiers";

// Años que puede elegir el selector. Se derivan de las reseñas fechadas del usuario autenticado en el
// backend (no se piden ni se confían desde el cliente), y la ruta conserva la misma puerta admin-only que
// el resto de la tier list.
function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/game-tiers/years`, { cache: "no-store" });
  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudieron cargar los años con reseñas");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const years = normalizeGameTierYears(await response.json());
  if (years === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una lista de años inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const result = NextResponse.json(years);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
