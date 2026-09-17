import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession, type AuthSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeSteamGame } from "@/lib/contracts/steam";

function parseAppId(value: string): number | null {
  const appId = Number(value);
  return Number.isSafeInteger(appId) && appId > 0 ? appId : null;
}

async function forward(
  request: Request,
  appIdRaw: string,
  method: "GET" | "POST",
  path: string
) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const appId = parseAppId(appIdRaw);
  if (appId === null) return badRequest(request, "AppID inválido");

  const url = new URL(`/api/steam/games/${appId}${path}`, getApiBaseUrl());

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method,
    cache: "no-store"
  });

  return buildResponse(request, response, updatedSession, session);
}

async function buildResponse(
  request: Request,
  response: Response,
  updatedSession: AuthSession,
  session: AuthSession
) {
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo cargar el juego");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const game = normalizeSteamGame(await response.json());
  if (game === null) {
    const result = upstreamError(request, 502, "Steam devolvió una respuesta inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(game);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function GET(request: Request, { params }: { params: Promise<{ appId: string }> }) {
  const { appId } = await params;
  return forward(request, appId, "GET", "");
}

export async function POST(request: Request, { params }: { params: Promise<{ appId: string }> }) {
  const { appId } = await params;
  return forward(request, appId, "POST", "/refresh");
}
