import { NextResponse } from "next/server";
import { parseMergeRequest } from "@/lib/contracts/games-merge";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

function parseId(value: string) {
  const id = Number(value);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

// Fusión manual de un juego canónico dentro de otro. El `absorbedGameId` de la URL desaparece; el
// `intoGameId` del cuerpo sobrevive. Las reseñas nunca se descartan: pasan al superviviente.
//
// El 409 del backend es un resultado NORMAL (identidad de Steam ambigua), no un error de red, así que la
// respuesta se reenvía con su status y su cuerpo tal cual: el cliente lo interpreta.
export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const { id: absorbedRaw } = await params;
  const absorbedGameId = parseId(absorbedRaw);
  if (absorbedGameId === null) return badRequest(request, "absorbedGameId inválido");

  const body = parseMergeRequest(await request.json().catch(() => null));
  if (body === null) {
    return badRequest(request, "La fusión necesita un intoGameId válido");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/games/${absorbedGameId}/merge`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      cache: "no-store"
    }
  );

  const raw = await response.text();
  const result = new NextResponse(raw, {
    status: response.status,
    headers: {
      "content-type": response.headers.get("content-type") ?? "application/json"
    }
  });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
