import { NextResponse } from "next/server";
import { normalizeManualSearchResponse } from "@/lib/contracts/manual-library";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Búsqueda de IGDB para el alta manual: solo lectura, el resultado se muestra y el id viaja de vuelta en
// el POST del alta. `source === null` llega tal cual al diálogo: significa «no disponible», no «sin
// resultados» (docs/PLAN_CONSOLE.md §5).
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const title = new URL(request.url).searchParams.get("title") ?? "";
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/library/manual/enrich?title=${encodeURIComponent(title)}`,
    { cache: "no-store" }
  );

  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo buscar en IGDB");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const report = normalizeManualSearchResponse(await response.json());
  if (report === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una búsqueda inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const out = NextResponse.json(report);
  await attachSessionCookie(out, updatedSession, session);
  return out;
}
