import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

// Detalle de una ejecución. El `details` ya viene saneado por la API: esta capa no lo reinterpreta, solo lo
// entrega normalizado para que la UI pueda expandirlo sin adivinar la forma.
export async function GET(request: Request, { params }: { params: Promise<{ jobRunId: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if ((session.user.role ?? "").toLowerCase() !== "admin") return forbidden(request);

  const { jobRunId } = await params;
  const id = Number(jobRunId);
  if (!Number.isInteger(id) || id <= 0) return badRequest(request, "Id de ejecución inválido");

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/jobs/${id}`,
    { method: "GET", cache: "no-store" }
  );

  if (!response.ok) {
    const result =
      response.status === 404
        ? upstreamError(request, 404, "La ejecución no existe")
        : upstreamError(request, response.status, "No se pudo cargar la ejecución");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const payload = await response.json();
  const result = NextResponse.json(payload);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
