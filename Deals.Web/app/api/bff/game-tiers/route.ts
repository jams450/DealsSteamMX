import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeGameTierList, parseGameTierUpdate } from "@/lib/contracts/game-tiers";

// La página de tier list es admin-only (`requireAdminSession`), así que el BFF cierra igual que abre:
// un usuario no admin no puede leer ni escribir sus puestos por esta ruta.
function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

function parseYear(input: string | null): number | null {
  if (input === null || !/^\d{4}$/.test(input)) return null;
  const year = Number(input);
  return year >= 1000 && year <= 9999 ? year : null;
}

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);
  const year = parseYear(new URL(request.url).searchParams.get("year"));
  if (year === null) return badRequest(request, "year debe ser un año de cuatro dígitos");

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/game-tiers?year=${year}`, { cache: "no-store" });
  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo cargar la tier list");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const entries = normalizeGameTierList(await response.json());
  if (entries === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una tier list inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const result = NextResponse.json(entries);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function PUT(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);
  const year = parseYear(new URL(request.url).searchParams.get("year"));
  if (year === null) return badRequest(request, "year debe ser un año de cuatro dígitos");
  // La partida reseñada es la identidad del puesto: un gameId no distingue dos partidas del mismo juego.
  const payload = parseGameTierUpdate(await request.json().catch(() => null));
  if (payload === null) return badRequest(request, "Envía reviewId, tier S–F y position válidos");

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/game-tiers?year=${year}`, {
    method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(payload), cache: "no-store"
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo guardar la tier list");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const result = new NextResponse(null, { status: 204 });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
