import { NextResponse } from "next/server";
import { normalizeReview, normalizeReviewListResponse, parseReviewCreateRequest } from "@/lib/contracts/reviews";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

// Las reseñas son del propio usuario que llama: a diferencia de la biblioteca, no son admin-only.
function parseGameId(value: string | null): number | null {
  if (value === null) return null;
  const gameId = Number(value);
  return Number.isSafeInteger(gameId) && gameId > 0 ? gameId : null;
}

async function upstreamMessage(response: Response, fallback: string) {
  const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
  return body?.message ?? body?.Message ?? fallback;
}

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const gameId = parseGameId(new URL(request.url).searchParams.get("gameId"));
  if (gameId === null) return badRequest(request, "gameId inválido");

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/reviews?gameId=${gameId}`,
    { method: "GET", cache: "no-store" }
  );

  if (!response.ok) {
    const result = upstreamError(request, response.status, await upstreamMessage(response, "No se pudieron cargar las reseñas"));
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const reviews = normalizeReviewListResponse(await response.json());
  if (reviews === null) {
    const result = upstreamError(request, 502, "El servidor devolvió reseñas inválidas");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(reviews);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const payload = parseReviewCreateRequest(await request.json().catch(() => null));
  if (payload === null) {
    return badRequest(
      request,
      "La reseña necesita gameId y platform válidos; la nota es un entero 0-100 y los meses son YYYY-MM"
    );
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/reviews`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
    cache: "no-store"
  });

  if (!response.ok) {
    const result = upstreamError(request, response.status, await upstreamMessage(response, "No se pudo guardar la reseña"));
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const review = normalizeReview(await response.json());
  if (review === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una reseña inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(review, { status: 201 });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
