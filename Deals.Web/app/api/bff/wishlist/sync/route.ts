import { NextResponse } from "next/server";
import { normalizeWishlistSyncResponse } from "@/app/wishlist/_lib/wishlist-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { unauthorized, upstreamError } from "@/lib/bff/http";

export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/wishlist/sync`, {
    method: "POST",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo sincronizar la wishlist");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const report = normalizeWishlistSyncResponse(await response.json());
  if (report === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un reporte de sincronización inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(report);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
