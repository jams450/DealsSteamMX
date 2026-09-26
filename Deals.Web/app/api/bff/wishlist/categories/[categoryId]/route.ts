import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

type Params = { params: Promise<{ categoryId: string }> };

async function forward(request: Request, categoryId: string, method: string, suffix = "", body?: unknown) {
  if (!/^\d+$/.test(categoryId) || Number(categoryId) <= 0) return badRequest(request, "categoryId inválido");
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/wishlist/categories/${categoryId}${suffix}`, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store"
  });
  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo actualizar categorías");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }
  const payload = response.status === 204 ? null : await response.json().catch(() => null);
  const result = payload === null ? new NextResponse(null, { status: response.status }) : NextResponse.json(payload, { status: response.status });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

function readBody(request: Request) { return request.json().catch(() => null) as Promise<{ name?: unknown; appIds?: unknown } | null>; }
function validateAppIds(body: { appIds?: unknown } | null): number[] | null {
  if (!Array.isArray(body?.appIds) || body.appIds.length === 0 || body.appIds.length > 200) return null;
  const ids = body.appIds.filter((id): id is number => typeof id === "number" && Number.isSafeInteger(id) && id > 0);
  return ids.length === body.appIds.length ? [...new Set(ids)] : null;
}

export async function PATCH(request: Request, { params }: Params) {
  const { categoryId } = await params;
  const body = await readBody(request);
  if (typeof body?.name !== "string" || !body.name.trim() || body.name.trim().length > 80) return badRequest(request, "name debe tener entre 1 y 80 caracteres");
  return forward(request, categoryId, "PATCH", "", { name: body.name });
}
export async function DELETE(request: Request, { params }: Params) { return forward(request, (await params).categoryId, "DELETE"); }
export async function POST(request: Request, { params }: Params) {
  const body = await readBody(request); const appIds = validateAppIds(body);
  if (!appIds) return badRequest(request, "appIds debe contener entre 1 y 200 enteros positivos");
  return forward(request, (await params).categoryId, "POST", "/items", { appIds });
}
export async function PUT(request: Request, { params }: Params) {
  const body = await readBody(request); const appIds = validateAppIds(body);
  if (!appIds) return badRequest(request, "appIds debe contener entre 1 y 200 enteros positivos");
  return forward(request, (await params).categoryId, "PUT", "/items", { appIds });
}
