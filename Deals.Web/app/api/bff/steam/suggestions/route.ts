import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeSteamSearch } from "@/lib/contracts/steam";

const MIN_QUERY_LENGTH = 2;

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const query = new URL(request.url).searchParams.get("query")?.trim();
  if (!query || query.length < MIN_QUERY_LENGTH) {
    return badRequest(request, `La consulta necesita al menos ${MIN_QUERY_LENGTH} caracteres`);
  }

  const url = new URL("/api/steam/suggestions", getApiBaseUrl());
  url.searchParams.set("query", query);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudieron cargar las sugerencias");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeSteamSearch(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
