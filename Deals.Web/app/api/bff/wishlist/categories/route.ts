import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

async function forward(request: Request, method: string, path: string, body?: unknown) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/wishlist/${path}`, {
    method, headers: body === undefined ? undefined : { "content-type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body), cache: "no-store"
  });
  if (!response.ok) { const result = upstreamError(request, response.status, "No se pudo actualizar categorías"); await attachSessionCookie(result, updatedSession, session); return result; }
  const result = NextResponse.json(await response.json().catch(() => null), { status: response.status }); await attachSessionCookie(result, updatedSession, session); return result;
}

export async function GET(request: Request) { return forward(request, "GET", "categories"); }
export async function POST(request: Request) {
  const body = await request.json().catch(() => null) as { name?: unknown } | null;
  if (typeof body?.name !== "string" || !body.name.trim() || body.name.trim().length > 80) return badRequest(request, "name debe tener entre 1 y 80 caracteres");
  return forward(request, "POST", "categories", { name: body.name });
}
