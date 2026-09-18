import { NextResponse } from "next/server";
import {
  MIN_VIABLE_DISCOUNT_PERCENT_MAX,
  MIN_VIABLE_DISCOUNT_PERCENT_MIN,
  normalizeWishlistPreferencesResponse
} from "@/app/wishlist/_lib/wishlist-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

export async function PUT(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const body = (await request.json().catch(() => null)) as { minViableDiscountPercent?: unknown } | null;
  const value = body?.minViableDiscountPercent;
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    value < MIN_VIABLE_DISCOUNT_PERCENT_MIN ||
    value > MIN_VIABLE_DISCOUNT_PERCENT_MAX
  ) {
    return badRequest(
      request,
      `minViableDiscountPercent debe ser un entero entre ${MIN_VIABLE_DISCOUNT_PERCENT_MIN} y ${MIN_VIABLE_DISCOUNT_PERCENT_MAX}`
    );
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/wishlist/preferences`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ minViableDiscountPercent: value }),
    cache: "no-store"
  });

  if (!response.ok) {
    const upstreamBody = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      upstreamBody?.message ?? upstreamBody?.Message ?? "No se pudo guardar el descuento mínimo viable"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const preferences = normalizeWishlistPreferencesResponse(await response.json());
  if (preferences === null) {
    const result = upstreamError(request, 502, "El servidor devolvió preferencias de wishlist inválidas");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(preferences);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
