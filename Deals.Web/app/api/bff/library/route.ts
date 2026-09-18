import { NextResponse } from "next/server";
import { normalizeLibraryResponse } from "@/app/library/_lib/library-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/library`, {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo cargar la biblioteca");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const library = normalizeLibraryResponse(await response.json());
  if (library === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una biblioteca inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(library);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
