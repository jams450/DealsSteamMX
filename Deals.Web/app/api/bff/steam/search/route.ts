import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeSteamSearch } from "@/lib/contracts/steam";

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const name = new URL(request.url).searchParams.get("name")?.trim();
  if (!name) return badRequest(request, "El nombre es requerido");

  const url = new URL("/api/steam/search", getApiBaseUrl());
  url.searchParams.set("query", name);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo buscar en Steam");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeSteamSearch(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
