import { NextResponse } from "next/server";
import { LIBRARY_IMPORT_MAX_BYTES, normalizeLibraryImportResponse } from "@/app/library/_lib/library-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// El tamaño se revisa antes de leer el cuerpo (content-length) y después (bytes reales: el header puede
// mentir o faltar). El archivo nunca se guarda: solo se reenvía el JSON a la API.
async function readImportBody(request: Request): Promise<unknown[] | null> {
  const declaredLength = Number(request.headers.get("content-length") ?? "");
  if (Number.isFinite(declaredLength) && declaredLength > LIBRARY_IMPORT_MAX_BYTES) {
    return null;
  }

  const raw = await request.text();
  if (new TextEncoder().encode(raw).length > LIBRARY_IMPORT_MAX_BYTES) {
    return null;
  }

  let payload: unknown;
  try {
    payload = JSON.parse(raw);
  } catch {
    return null;
  }

  return Array.isArray(payload) ? payload : null;
}

export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const payload = await readImportBody(request);
  if (payload === null) {
    return badRequest(request, "El cuerpo debe ser un arreglo JSON de hasta 10 MiB, como el export de Playnite");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/library/import`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(request, response.status, body?.message ?? body?.Message ?? "No se pudo importar la biblioteca");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const report = normalizeLibraryImportResponse(await response.json());
  if (report === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un reporte de importación inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(report);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
