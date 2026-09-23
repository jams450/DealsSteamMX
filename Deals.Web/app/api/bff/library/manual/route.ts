import { NextResponse } from "next/server";
import { normalizeManualAddResponse } from "@/lib/contracts/manual-library";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Alta manual de un juego (docs/PLAN_CONSOLE.md §5). El cuerpo es JSON plano y pequeño: se comprueba el
// formato aquí y la API vuelve a validar todo (plataforma, título, ids). Nunca lleva URLs: la portada la
// resuelve el servidor por el id de IGDB elegido en la búsqueda.
export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const raw = await request.text();
  if (raw.length > 16_384) return badRequest(request, "El cuerpo es demasiado grande");

  let payload: unknown;
  try {
    payload = JSON.parse(raw);
  } catch {
    return badRequest(request, "El cuerpo debe ser JSON");
  }
  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) {
    return badRequest(request, "El cuerpo debe ser un objeto");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/library/manual`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo añadir el juego");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const report = normalizeManualAddResponse(await response.json());
  if (report === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un resultado de alta inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const out = NextResponse.json(report);
  await attachSessionCookie(out, updatedSession, session);
  return out;
}
