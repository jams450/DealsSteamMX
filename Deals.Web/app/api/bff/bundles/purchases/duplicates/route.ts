import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeBundlePurchaseDuplicates } from "@/lib/contracts/bundle-purchases";

// Keys duplicadas del usuario: cada fila es un juego que llegó por un bundle y ya estaba en la biblioteca
// en la misma plataforma. Solo lectura. El endpoint no devuelve códigos de activación y este BFF tampoco
// los pide: el contrato habla de juegos duplicados, no de llaves.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/bundles/purchases/duplicates`,
    { method: "GET", cache: "no-store" }
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as {
      message?: string;
      Message?: string;
      title?: string;
      Title?: string;
    } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? body?.title ?? body?.Title ?? "No se pudieron cargar los juegos duplicados"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // Una lista inválida no se pinta como «no hay duplicados»: ocultaría duplicados reales.
  const duplicates = normalizeBundlePurchaseDuplicates(await response.json().catch(() => null));
  if (duplicates === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una lista de duplicados inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(duplicates);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
