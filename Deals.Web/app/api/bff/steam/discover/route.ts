import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeSteamDiscover } from "@/lib/contracts/steam";

const DISCOVER_LISTS = ["discount", "historic", "recent"] as const;

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const params = new URL(request.url).searchParams;
  const list = (params.get("list") ?? "discount").trim().toLowerCase();
  if (!(DISCOVER_LISTS as readonly string[]).includes(list)) {
    return badRequest(request, "Lista desconocida: usa discount, historic o recent");
  }

  const url = new URL("/api/steam/discover", getApiBaseUrl());
  url.searchParams.set("list", list);
  const page = params.get("page");
  const pageSize = params.get("pageSize");
  if (page) url.searchParams.set("page", page);
  if (pageSize) url.searchParams.set("pageSize", pageSize);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo cargar el descubrimiento");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeSteamDiscover(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
