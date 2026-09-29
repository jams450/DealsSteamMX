import { NextResponse } from "next/server";
import { normalizeSteamGridDbCoverSearch } from "@/lib/contracts/library-covers";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

// Candidatos de SteamGridDB para el selector manual de portada: solo lectura, sin URLs. `source === null`
// llega tal cual al selector: significa «no disponible», no «sin candidatos». El id elegido viaja después en
// el PUT de la portada, que es el único que escribe.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if (!isAdmin(session.user.role)) return forbidden(request);

  const title = new URL(request.url).searchParams.get("title") ?? "";
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(
    session,
    `${getApiBaseUrl()}/api/library/covers/steamgriddb/search?title=${encodeURIComponent(title)}`,
    { cache: "no-store" }
  );

  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo buscar en SteamGridDB");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const search = normalizeSteamGridDbCoverSearch(await response.json());
  if (search === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una búsqueda inválida");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const out = NextResponse.json(search);
  await attachSessionCookie(out, updatedSession, session);
  return out;
}
