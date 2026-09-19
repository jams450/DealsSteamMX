import { NextResponse } from "next/server";
import { normalizeCoverUrl, parseCoverPick } from "@/lib/contracts/library-covers";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

function parseId(value: string) {
  const id = Number(value);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

// Elección manual de portada: el cliente manda el appid de Steam que el usuario escogió y el servidor
// resuelve y guarda la URL. Es la única ruta que reemplaza una portada existente.
// El segmento se llama `id`, como el de `[id]/merge`: Next no admite dos nombres para el mismo nivel
// dinámico, así que el gameId se lee de `id`.
export async function PUT(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const { id: gameIdRaw } = await params;
  const gameId = parseId(gameIdRaw);
  if (gameId === null) return badRequest(request, "gameId inválido");

  const payload = parseCoverPick(await request.json().catch(() => null));
  if (payload === null) {
    return badRequest(request, "La portada necesita un steamAppId válido");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/games/${gameId}/cover`,
    {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
      cache: "no-store"
    }
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudo guardar la portada"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const imageUrl = normalizeCoverUrl(await response.json());
  if (imageUrl === null) {
    const result = upstreamError(request, 502, "El servidor no devolvió la portada guardada");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json({ imageUrl });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
