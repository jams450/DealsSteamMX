import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeReview,
  normalizeReviewListResponse,
  type Review,
  type ReviewCreateRequest,
  type ReviewUpdateRequest
} from "@/lib/contracts/reviews";

const INVALID_REVIEW = "El servidor devolvió una reseña inválida";

export async function getReviews(gameId: number): Promise<readonly Review[]> {
  const response = await fetch(`/api/bff/reviews?gameId=${gameId}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar las reseñas");

  const reviews = normalizeReviewListResponse(await response.json());
  if (reviews === null) throw new Error("El servidor devolvió una lista de reseñas inválida");
  return reviews;
}

export async function createReview(input: ReviewCreateRequest): Promise<Review> {
  const response = await csrfFetch("/api/bff/reviews", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(input),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo guardar la reseña");

  const review = normalizeReview(await response.json());
  if (review === null) throw new Error(INVALID_REVIEW);
  return review;
}

export async function updateReview(reviewId: number, input: ReviewUpdateRequest): Promise<Review> {
  const response = await csrfFetch(`/api/bff/reviews/${reviewId}`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(input),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo guardar la reseña");

  const review = normalizeReview(await response.json());
  if (review === null) throw new Error(INVALID_REVIEW);
  return review;
}

export async function deleteReview(reviewId: number): Promise<void> {
  const response = await csrfFetch(`/api/bff/reviews/${reviewId}`, { method: "DELETE", cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo borrar la reseña");
}
