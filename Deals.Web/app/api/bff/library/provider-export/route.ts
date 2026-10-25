import { NextResponse } from "next/server";
import {
  HEROIC_LIBRARY_EXPORT_FORMAT,
  HEROIC_LIBRARY_EXPORT_MAX_BYTES,
  HEROIC_LIBRARY_EXPORT_VERSION,
  parseHeroicLibraryExport
} from "@/lib/contracts/heroic-library-export";
import { normalizeLibraryImportResponse } from "@/app/library/_lib/library-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

const INVALID_BODY =
  `El archivo debe ser un export de Heroic (format "${HEROIC_LIBRARY_EXPORT_FORMAT}", version ${HEROIC_LIBRARY_EXPORT_VERSION}, source "heroic") de hasta 10 MiB`;

// Puerta dedicada de la exportación de Heroic (odd/tasks/heroic-library-export.md, S3). No comparte contrato
// con la importación de Playnite: aquí el cuerpo es un sobre versionado, no un arreglo de filas, así que
// ningún archivo puede colarse por la otra puerta. El mismo límite de bytes se comprueba dos veces (el
// header puede mentir o faltar) y el archivo nunca se guarda: solo se reenvía el sobre ya normalizado, que
// es también la lista blanca de campos que llega a la API.
async function readExportBody(request: Request): Promise<unknown | null> {
  const declaredLength = Number(request.headers.get("content-length") ?? "");
  if (Number.isFinite(declaredLength) && declaredLength > HEROIC_LIBRARY_EXPORT_MAX_BYTES) {
    return null;
  }

  const raw = await request.text();
  if (new TextEncoder().encode(raw).length > HEROIC_LIBRARY_EXPORT_MAX_BYTES) {
    return null;
  }

  try {
    return JSON.parse(raw) as unknown;
  } catch {
    return null;
  }
}

export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const parsed = parseHeroicLibraryExport(await readExportBody(request));
  if (parsed === null) return badRequest(request, INVALID_BODY);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/library/provider-export`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(parsed),
      cache: "no-store"
    }
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudo importar la biblioteca de Heroic"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // El reporte es el mismo que el de Playnite (imported/updated/unresolved/unsupportedSource/byStore): es el
  // mismo escrito y la página lo pinta con el mismo componente.
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
