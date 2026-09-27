import { NextResponse } from "next/server";
import { MAX_PACKAGE_APP_IDS } from "@/app/wishlist/_lib/wishlist-package";
import { normalizeWishlistPackagePreview } from "@/app/wishlist/_lib/wishlist-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

// Preview del paquete: cuánto suman los juegos seleccionados en oficial y en keys. Solo viaja la selección;
// ningún importe entra ni sale del cliente sin pasar por la API, que es la que relee sus precios.
export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const body = (await request.json().catch(() => null)) as { appIds?: unknown } | null;
  const rawAppIds = body?.appIds;
  if (!Array.isArray(rawAppIds) || rawAppIds.length === 0) {
    return badRequest(request, "appIds debe ser un arreglo con al menos un AppID");
  }

  // La forma se valida aquí además de en la API: un cuerpo que no puede ser válido no merece una llamada
  // río arriba, y el usuario recibe el mismo `BAD_REQUEST` tipado que ya sabe leer.
  const appIds: number[] = [];
  for (const entry of rawAppIds) {
    if (typeof entry !== "number" || !Number.isSafeInteger(entry) || entry <= 0) {
      return badRequest(request, "Cada AppID debe ser un entero positivo");
    }
    if (!appIds.includes(entry)) appIds.push(entry);
  }

  if (appIds.length > MAX_PACKAGE_APP_IDS) {
    return badRequest(request, `No se pueden calcular más de ${MAX_PACKAGE_APP_IDS} juegos a la vez`);
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/wishlist/package-preview`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ appIds }),
      cache: "no-store"
    },
    request.signal
  );

  if (!response.ok) {
    await response.text().catch(() => "");
    const result = upstreamError(request, response.status, "No se pudo calcular el paquete");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const preview = normalizeWishlistPackagePreview(await response.json());
  if (preview === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un cálculo de paquete inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(preview);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
