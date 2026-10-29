import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeBundlePurchasePreview, parseBundleIdentifier } from "@/lib/contracts/bundle-purchases";
import { normalizeStore } from "@/lib/contracts/stores";

// Vista previa de la compra: el snapshot que la confirmación va a reenviar. Solo lectura (no escribe ni
// reserva nada), así que puede pedirse tantas veces como haga falta: es lo que hace el diálogo cuando el
// servidor responde 400 por un snapshot viejo.
//
// `platform` es opcional: solo ajusta la información de posesión que el servidor calcula. Si viaja, debe
// ser una llave ya canónica de StoreKeys (el backend conoce las tiendas y los slugs de plataforma); un
// valor inventado se rechaza aquí en vez de dejar que el backend adivine.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const incoming = new URL(request.url).searchParams;
  const source = parseBundleIdentifier(incoming.get("source"));
  const bundleKey = parseBundleIdentifier(incoming.get("bundleKey"));
  if (source === null || bundleKey === null) {
    return badRequest(request, "La vista previa necesita source y bundleKey válidos");
  }

  const rawPlatform = incoming.get("platform");
  const platform = rawPlatform === null ? null : normalizeStore(rawPlatform);
  if (rawPlatform !== null && (platform === null || platform !== rawPlatform)) {
    return badRequest(request, "La plataforma de la vista previa no es una tienda conocida");
  }

  const url = new URL("/api/bundles/purchases/preview", getApiBaseUrl());
  url.searchParams.set("source", source);
  url.searchParams.set("bundleKey", bundleKey);
  if (platform !== null) url.searchParams.set("platform", platform);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as {
      message?: string;
      Message?: string;
      title?: string;
      Title?: string;
    } | null;
    const fallback =
      response.status === 404
        ? "Ese bundle ya no existe en el catálogo guardado"
        : "No se pudo leer la vista previa del bundle";
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? body?.title ?? body?.Title ?? fallback
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // Sin snapshot válido no hay nada que confirmar: se trata como upstream inválido, nunca como éxito.
  const preview = normalizeBundlePurchasePreview(await response.json().catch(() => null));
  if (preview === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una vista previa de compra inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(preview);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
