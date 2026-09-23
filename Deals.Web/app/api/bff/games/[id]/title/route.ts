import { NextResponse } from "next/server";
import { normalizeGameTitleApplied, parseGameTitleRequest } from "@/lib/contracts/game-title";
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

// Mensaje del backend para el panel de error. Los fallos controlados (400/404/503) llegan como
// ProblemDetails (`detail`/`title`); el `message` se tolera porque los DTOs propios lo usan.
async function readUpstreamMessage(response: Response, fallback: string) {
  const body = (await response.json().catch(() => null)) as
    | { message?: string; Message?: string; detail?: string; Detail?: string; title?: string; Title?: string }
    | null;

  return (
    body?.message ?? body?.Message ?? body?.detail ?? body?.Detail ?? body?.title ?? body?.Title ?? fallback
  );
}

// Edición del título canónico de un juego: `games.title` / `games.normalized_title`, nunca el
// `user_library.title` importado de Playnite. El cuerpo es discriminado y se valida aquí antes de gastar
// el viaje: `manual` con el título escrito, o `igdb` con el id cuya lectura hace el servidor.
//
// Un 409 es un resultado NORMAL — el catálogo rechazó la identidad y no escribió nada — así que se
// reenvía con su status y su cuerpo tal cual (`applied: false` + `reason`), como la fusión de al lado. El
// resto de los no-2xx sí son errores y pasan por `upstreamError`, que conserva el status del backend.
export async function PUT(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const { id: gameIdRaw } = await params;
  const gameId = parseId(gameIdRaw);
  if (gameId === null) return badRequest(request, "gameId inválido");

  const payload = parseGameTitleRequest(await request.json().catch(() => null));
  if (payload === null) {
    return badRequest(request, "La edición necesita mode 'manual' con title, o 'igdb' con igdbId");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/games/${gameId}/title`,
    {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
      cache: "no-store"
    }
  );

  if (response.status === 409) {
    const raw = await response.text();
    const result = new NextResponse(raw, {
      status: 409,
      headers: { "content-type": response.headers.get("content-type") ?? "application/json" }
    });
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  if (!response.ok) {
    const result = upstreamError(
      request,
      response.status,
      await readUpstreamMessage(response, "No se pudo guardar el título")
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // El cuerpo que se reenvía es el que el catálogo guardó, no el que el cliente mandó: la grilla debe
  // pintar `title` normalizado por el servidor. Una forma que no se pueda leer es 502, nunca un 200 vacío.
  const applied = normalizeGameTitleApplied(await response.json().catch(() => null));
  if (applied === null) {
    const result = upstreamError(request, 502, "El servidor no devolvió el título guardado");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json({
    gameId: applied.gameId,
    title: applied.title,
    normalizedTitle: applied.normalizedTitle,
    source: applied.source,
    igdbId: applied.igdbId
  });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
