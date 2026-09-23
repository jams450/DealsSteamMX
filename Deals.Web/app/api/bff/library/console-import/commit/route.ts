import { NextResponse } from "next/server";
import { CONSOLE_IMPORT_MAX_BYTES, normalizeConsoleImportCommit } from "@/lib/contracts/console-import";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { readJsonArrayBody } from "@/lib/bff/json-array-body";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Commit de la importación masiva de consolas (docs/PLAN_CONSOLE.md §7): solo admin. El cuerpo son las
// decisiones explícitas (plataforma elegida + una única acción de identidad por fila).
//
// El 409 es una *respuesta estructurada*, no un error del BFF: la API rechaza el payload entero y
// devuelve los conflictos de identidad en el mismo DTO del commit. Se reenvía tal cual para que la UI
// pueda explicar qué fila choca con qué juego; convertirlo en el sobre de error perdería el detalle.
export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const payload = await readJsonArrayBody(request, CONSOLE_IMPORT_MAX_BYTES);
  if (payload === null) {
    return badRequest(request, "El cuerpo debe ser un arreglo JSON de hasta 10 MiB con las decisiones de importación");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/library/console-import/commit`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      cache: "no-store"
    }
  );

  if (response.status === 409) {
    const refusal = normalizeConsoleImportCommit(await response.json().catch(() => null));
    if (refusal === null) {
      const result = upstreamError(request, 502, "El servidor devolvió un conflicto de identidad inválido");
      await attachSessionCookie(result, updatedSession, session);
      return result;
    }

    const refused = NextResponse.json(refusal, { status: 409 });
    await attachSessionCookie(refused, updatedSession, session);
    return refused;
  }

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudo importar la biblioteca de consolas"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const report = normalizeConsoleImportCommit(await response.json());
  if (report === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un resultado de importación inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const out = NextResponse.json(report);
  await attachSessionCookie(out, updatedSession, session);
  return out;
}
