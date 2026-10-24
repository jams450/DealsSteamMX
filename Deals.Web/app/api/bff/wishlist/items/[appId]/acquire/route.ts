import { NextResponse } from "next/server";
import { normalizeAcquireRequestBody } from "@/app/wishlist/_lib/wishlist-acquire";
import { normalizeWishlistAcquireResponse } from "@/app/wishlist/_lib/wishlist-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

type Params = { params: Promise<{ appId: string }> };

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Alta de un juego de la wishlist en la biblioteca (L4). La identidad es el AppID de la URL: aquí no viaja
// ningún título, así que este camino no puede casar por nombre. El cuerpo se valida con el mismo parser
// estricto del diálogo y la API vuelve a validar todo (identidad canónica, plataforma, tipo y fecha); el BFF
// solo reenvía el cuerpo ya normalizado y traduce la respuesta para que el cliente nunca vea un 200 con
// forma rara. Admin: la página ya exige sesión de admin, esto lo repite donde se escribe.
//
// S6/L14: `acquiredStoreType` y `acquisitionProvider` viajan como campos aditivos opcionales. Un cliente viejo
// que no los manda sigue funcionando: el parser los normaliza a `null` (nunca al default de la UI) y el servidor
// los guarda en null, sin adivinar clasificación ni proveedor. Un valor fuera del vocabulario se rechaza aquí con
// 400, antes de gastar la llamada.
export async function POST(request: Request, { params }: Params) {
  const { appId } = await params;
  const numericAppId = Number(appId);
  if (!/^\d+$/.test(appId) || !Number.isSafeInteger(numericAppId) || numericAppId <= 0) {
    return badRequest(request, "appId inválido");
  }

  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const raw = await request.text();
  if (raw.length > 4096) return badRequest(request, "El cuerpo es demasiado grande");

  let payload: unknown;
  try {
    payload = JSON.parse(raw);
  } catch {
    return badRequest(request, "El cuerpo debe ser JSON");
  }

  const body = normalizeAcquireRequestBody(payload);
  if (body === null) {
    return badRequest(
      request,
      "El cuerpo debe incluir platform, acquiredStore, acquisitionType, acquiredAt («AAAA-MM-DD» o null) y removeFromWishlist; " +
        "acquiredStoreType (official|keyshop|unknown) y acquisitionProvider (itad|ggdeals|direct|other) son opcionales o null"
    );
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/wishlist/items/${numericAppId}/acquire`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      cache: "no-store"
    },
    request.signal
  );

  if (!response.ok) {
    // La API responde ProblemDetails (título en español) en los 400 y un 404 mudo cuando el AppID ya no está
    // en la wishlist del usuario o todavía no tiene identidad canónica. Se prefiere el texto del servidor y,
    // si no hay, se explica el caso en vez de un «error» genérico que el usuario no puede accionar.
    const upstream = (await response.json().catch(() => null)) as {
      message?: string;
      Message?: string;
      title?: string;
      Title?: string;
    } | null;
    const fallback =
      response.status === 404
        ? "Ese juego ya no está en tu wishlist o todavía no tiene ficha canónica"
        : "No se pudo añadir el juego a la biblioteca";
    const result = upstreamError(
      request,
      response.status,
      upstream?.message ?? upstream?.Message ?? upstream?.title ?? upstream?.Title ?? fallback
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const acquired = normalizeWishlistAcquireResponse(await response.json());
  if (acquired === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un resultado de alta inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(acquired);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
