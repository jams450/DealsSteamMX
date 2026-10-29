import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import {
  CANDIDATE_LIMIT_DEFAULT,
  normalizeCatalogCandidates,
  parseCatalogCandidateLimit,
  parseCatalogCandidateQuery
} from "@/lib/contracts/bundle-purchases";

// Candidatos de ficha canónica para vincular a mano un ítem del bundle que no tiene identidad. Solo
// lectura: el id elegido viaja después en el cuerpo de la compra, que es el único que escribe.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const incoming = new URL(request.url).searchParams;
  const query = parseCatalogCandidateQuery(incoming.get("q"));
  if (query === null) {
    return badRequest(request, "Escribe al menos 2 caracteres para buscar en el catálogo");
  }
  const limit = parseCatalogCandidateLimit(incoming.get("limit")) ?? CANDIDATE_LIMIT_DEFAULT;

  const url = new URL("/api/bundles/catalog-candidates", getApiBaseUrl());
  url.searchParams.set("q", query);
  url.searchParams.set("limit", String(limit));

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as {
      message?: string;
      Message?: string;
      title?: string;
      Title?: string;
    } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? body?.title ?? body?.Title ?? "No se pudo buscar en el catálogo"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // Los candidatos son sugerencias: una fila rara se descarta y la búsqueda sigue siendo válida.
  const result = NextResponse.json(normalizeCatalogCandidates(await response.json().catch(() => null)));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
