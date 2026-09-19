import { NextResponse } from "next/server";
import { normalizeDuplicateGroups } from "@/lib/contracts/games-merge";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Solo lectura: la lista de grupos duplicados que la herramienta de mantenimiento revisa a mano. La
// fusión vive en `/api/bff/games/[id]/merge` porque es una escritura destructiva.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/games/merge-suggestions`,
    { method: "GET", cache: "no-store" }
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudieron cargar los duplicados");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // Una respuesta con forma inválida no se puede leer como "no hay duplicados": es un error de upstream.
  const groups = normalizeDuplicateGroups(await response.json());
  if (groups === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una lista de duplicados inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(groups);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
