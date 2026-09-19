import { NextResponse } from "next/server";
import { parseFavoriteTarget } from "@/lib/contracts/favorites";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";

// Interruptor de favorito del propio usuario. POST marca, DELETE desmarca: no hay cuerpo en la URL porque
// la identidad viaja en el cuerpo (`gameId` o `steamAppId`), y ambos verbos son de mutación, así que el
// middleware CSRF ya los cubre.
async function toggle(request: Request, method: "POST" | "DELETE") {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const target = parseFavoriteTarget(await request.json().catch(() => null));
  if (target === null) {
    return badRequest(request, "Envía gameId o steamAppId, no ambos ni ninguno");
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/favorites`, {
    method,
    headers: { "content-type": "application/json" },
    body: JSON.stringify(target),
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudo actualizar el favorito"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // 204 sin cuerpo: el estado del favorito lo decide quien llama, que ya lo cambió en pantalla.
  const result = new NextResponse(null, { status: 204 });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function POST(request: Request) {
  return toggle(request, "POST");
}

export async function DELETE(request: Request) {
  return toggle(request, "DELETE");
}
