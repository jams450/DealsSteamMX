import { NextResponse } from "next/server";
import { CONSOLE_IMPORT_MAX_BYTES, normalizeConsoleImportPreview } from "@/lib/contracts/console-import";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { readJsonArrayBody } from "@/lib/bff/json-array-body";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Preview de la importación masiva de consolas (docs/PLAN_CONSOLE.md §7): solo lectura y solo admin,
// misma política que la API. El cuerpo son las filas de consola del export de Playnite (las que no
// tienen `Source`); las de tienda se descartan antes de llegar aquí y su puerta sigue siendo
// `POST /api/bff/library/import`.
export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const payload = await readJsonArrayBody(request, CONSOLE_IMPORT_MAX_BYTES);
  if (payload === null) {
    return badRequest(request, "El cuerpo debe ser un arreglo JSON de hasta 10 MiB con las filas de consola del export de Playnite");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/library/console-import/preview`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      cache: "no-store"
    }
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudo previsualizar la importación de consolas"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const preview = normalizeConsoleImportPreview(await response.json());
  if (preview === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un preview de consolas inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const out = NextResponse.json(preview);
  await attachSessionCookie(out, updatedSession, session);
  return out;
}
