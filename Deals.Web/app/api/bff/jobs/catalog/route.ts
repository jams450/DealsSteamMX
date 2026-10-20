import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeJobCatalog } from "@/lib/contracts/jobs";

// Catálogo de jobs registrados en el host. Incluye los deshabilitados con su motivo: la página debe poder
// decir «no se ejecuta en este host» en vez de esconderlo o fingir que sí.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if ((session.user.role ?? "").toLowerCase() !== "admin") return forbidden(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/jobs/catalog`,
    { method: "GET", cache: "no-store" }
  );

  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo cargar el catálogo de jobs");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeJobCatalog(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
