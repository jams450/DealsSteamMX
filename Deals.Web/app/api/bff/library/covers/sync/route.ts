import { NextResponse } from "next/server";
import { normalizeCoverSyncReport, parseCoverSyncRequest } from "@/lib/contracts/library-covers";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Pasada de relleno de portadas sobre la biblioteca del propio admin. El límite viaja tal cual y el
// backend lo valida otra vez: el cuerpo no lleva nada más, y nunca una URL.
export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const payload = parseCoverSyncRequest(await request.json().catch(() => null));
  if (payload === null) {
    return badRequest(request, "El límite debe ser un entero entre 1 y 100 juegos");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/library/covers/sync`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
      cache: "no-store"
    }
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudieron sincronizar las portadas"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const report = normalizeCoverSyncReport(await response.json());
  if (report === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un reporte de portadas inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(report);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
