import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

type Params = { params: Promise<{ appId: string }> };

export async function PUT(request: Request, { params }: Params) {
  const { appId } = await params;
  if (!/^\d+$/.test(appId) || Number(appId) <= 0) return badRequest(request, "appId inválido");
  const body = await request.json().catch(() => null) as { categoryIds?: unknown } | null;
  if (!Array.isArray(body?.categoryIds) || body.categoryIds.length > 200 || body.categoryIds.some((id) => typeof id !== "number" || !Number.isSafeInteger(id) || id <= 0)) {
    return badRequest(request, "categoryIds debe contener como máximo 200 enteros positivos");
  }
  const categoryIds = [...new Set(body.categoryIds as number[])];
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/wishlist/items/${appId}/categories`, {
    method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ categoryIds }), cache: "no-store"
  });
  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudieron actualizar las categorías del juego");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const result = new NextResponse(null, { status: response.status });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
