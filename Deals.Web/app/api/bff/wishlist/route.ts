import { NextResponse } from "next/server";
import { normalizeWishlistResponse } from "@/app/wishlist/_lib/wishlist-contract";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { unauthorized, upstreamError } from "@/lib/bff/http";

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const upstreamUrl = new URL("/api/wishlist", getApiBaseUrl());
  const params = new URL(request.url).searchParams;
  for (const key of ["page", "pageSize", "search", "minPrice", "maxPrice", "owned", "subscription", "uncategorized", "sort", "direction"]) {
    const value = params.get(key);
    if (value !== null) upstreamUrl.searchParams.set(key, value);
  }
  for (const categoryId of params.getAll("categoryIds")) upstreamUrl.searchParams.append("categoryIds", categoryId);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, upstreamUrl.toString(), {
    method: "GET",
    cache: "no-store"
  }, request.signal);

  if (!response.ok) {
    await response.text().catch(() => "");
    const result = upstreamError(request, response.status, "No se pudo cargar la wishlist");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const wishlist = normalizeWishlistResponse(await response.json());
  if (wishlist === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una wishlist inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(wishlist);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
