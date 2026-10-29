import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import {
  normalizeBundlePurchaseList,
  normalizeBundlePurchaseRequest,
  normalizeBundlePurchaseResult
} from "@/lib/contracts/bundle-purchases";

// Historial de compras del usuario y alta de una compra (S3-S5). La identidad del usuario nunca viaja en
// la petición: la pone la sesión y el backend la deriva del token. El cuerpo se valida aquí con el mismo
// contrato que usa el diálogo, así que un payload mal formado no gasta la llamada.
//
// Los estados del backend tienen significado de negocio y se conservan: 400 (snapshot viejo, conteo
// equivocado, vínculo manual inválido), 404 (el bundle ya no está) y 409 (otra operación lo está
// registrando). El cliente decide: 400 ⇒ volver a pedir la vista previa; 409 ⇒ reintentar.
const MAX_BODY_BYTES = 65_536;

function upstreamMessage(body: unknown): string | null {
  if (typeof body !== "object" || body === null) return null;
  const record = body as Record<string, unknown>;
  const candidate = record.message ?? record.Message ?? record.title ?? record.Title ?? record.detail;
  return typeof candidate === "string" && candidate.trim() ? candidate.trim() : null;
}

function fallbackMessage(status: number): string {
  if (status === 404) return "Ese bundle ya no está disponible para registrar su compra.";
  if (status === 409) return "Otra operación está registrando este bundle. Reintenta en un momento.";
  if (status === 400) return "El servidor rechazó la compra: revisa el tier, la selección y la fecha.";
  return "No se pudo registrar la compra del bundle.";
}

async function relayError(request: Request, response: Response, fallback: string) {
  const body = await response.json().catch(() => null);
  return { message: upstreamMessage(body) ?? fallback, status: response.status };
}

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/bundles/purchases`,
    { method: "GET", cache: "no-store" }
  );

  if (!response.ok) {
    const relay = await relayError(request, response, "No se pudo cargar tu historial de compras");
    const result = upstreamError(request, relay.status, relay.message);
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // Un historial inválido no se pinta como «vacío»: sería una compra que desaparece.
  const list = normalizeBundlePurchaseList(await response.json().catch(() => null));
  if (list === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un historial de compras inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(list);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) return badRequest(request, "El cuerpo es demasiado grande");

  let payload: unknown;
  try {
    payload = JSON.parse(raw);
  } catch {
    return badRequest(request, "El cuerpo debe ser JSON");
  }

  const body = normalizeBundlePurchaseRequest(payload);
  if (body === null) {
    return badRequest(
      request,
      "La compra necesita source, bundleKey, tierKey, platform (llave de tienda), snapshotHash (64 hex), " +
        "acquiredAt (AAAA-MM-DD), acquiredStore, acquiredStoreType (official|keyshop|unknown), " +
        "acquisitionProvider (itad|ggdeals|direct|other) y al menos un ítem con itemKey único; " +
        "el platform y el canonicalGameId de cada ítem son opcionales"
    );
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/bundles/purchases`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      cache: "no-store"
    },
    request.signal
  );

  if (!response.ok) {
    const relay = await relayError(request, response, fallbackMessage(response.status));
    const result = upstreamError(request, relay.status, relay.message);
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // La compra ya ocurrió: solo un resultado sin purchaseId utilizable es una respuesta inválida.
  const purchase = normalizeBundlePurchaseResult(await response.json().catch(() => null));
  if (purchase === null) {
    const result = upstreamError(request, 502, "El servidor devolvió un resultado de compra inválido");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(purchase);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
