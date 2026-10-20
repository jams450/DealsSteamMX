import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

// Única ruta que pide trabajo real. Devuelve el `jobRunId` de la API (202) para que la página siga esa
// ejecución; el trabajo ocurre en el despachador del host, no dentro de esta petición.
//
// Las opciones se reenvían tal cual las declaró el catálogo: quien valida contra el contrato es la API. Aquí
// solo se comprueba la forma del sobre para no enviar basura.
export async function POST(request: Request, { params }: { params: Promise<{ job: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if ((session.user.role ?? "").toLowerCase() !== "admin") return forbidden(request);

  const { job } = await params;
  if (!job || job.length > 64 || !/^[a-z0-9][a-z0-9._-]*$/.test(job)) {
    return badRequest(request, "Job inválido");
  }

  const body = (await request.json().catch(() => null)) as
    | { options?: unknown; confirm?: unknown }
    | null;
  if (body !== null && (typeof body !== "object" || Array.isArray(body))) {
    return badRequest(request, "Cuerpo de la petición inválido");
  }

  const payload = {
    options: body?.options ?? null,
    confirm: body?.confirm === true
  };

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/jobs/${encodeURIComponent(job)}/run`,
    {
      method: "POST",
      cache: "no-store",
      body: JSON.stringify(payload)
    }
  );

  if (!response.ok) {
    const error = (await response.json().catch(() => null)) as
      | { detail?: string; title?: string; message?: string; Message?: string }
      | null;
    const message =
      error?.detail ??
      error?.message ??
      error?.Message ??
      error?.title ??
      "No se pudo iniciar la ejecución";
    const result = upstreamError(request, response.status, message, response.headers);
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(await response.json(), { status: 202 });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
