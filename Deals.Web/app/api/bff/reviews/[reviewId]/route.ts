import { NextResponse } from "next/server";
import { normalizeReview, parseReviewUpdateRequest } from "@/lib/contracts/reviews";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

function parseReviewId(value: string): number | null {
  const reviewId = Number(value);
  return Number.isSafeInteger(reviewId) && reviewId > 0 ? reviewId : null;
}

async function upstreamMessage(response: Response, fallback: string) {
  const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
  return body?.message ?? body?.Message ?? fallback;
}

export async function PUT(request: Request, { params }: { params: Promise<{ reviewId: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const reviewId = parseReviewId((await params).reviewId);
  if (reviewId === null) return badRequest(request, "reviewId inválido");

  // La identidad es inmutable: solo viajan los campos editables, ya validados.
  const payload = parseReviewUpdateRequest(await request.json().catch(() => null));
  if (payload === null) {
    return badRequest(request, "La nota es un entero 0-100 y los meses son YYYY-MM");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/reviews/${reviewId}`, {
    method: "PUT",
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

  const result = NextResponse.json(review);
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function DELETE(request: Request, { params }: { params: Promise<{ reviewId: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const reviewId = parseReviewId((await params).reviewId);
  if (reviewId === null) return badRequest(request, "reviewId inválido");

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/reviews/${reviewId}`, {
    method: "DELETE",
    cache: "no-store"
  });

  if (!response.ok) {
    const result = upstreamError(request, response.status, await upstreamMessage(response, "No se pudo borrar la reseña"));
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // 204 sin cuerpo: el borrado no devuelve nada que normalizar.
  const result = new NextResponse(null, { status: 204 });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
