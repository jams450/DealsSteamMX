import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { upstreamError, unauthorized } from "@/lib/bff/http";
import { normalizeGlobalDealCatalog, parseGlobalDealPageParam } from "@/lib/contracts/global-deals";

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  // Solo se reenvían página y tamaño si son enteros positivos válidos: la basura se descarta y el
  // backend aplica su default. El usuario nunca viaja en la query; el backend lo deriva de la sesión.
  // No hay POST: el feed es de solo lectura y el barrido del proveedor nunca corre por render.
  const incoming = new URL(request.url).searchParams;
  const url = new URL("/api/deals", getApiBaseUrl());
  const page = parseGlobalDealPageParam(incoming.get("page"));
  const pageSize = parseGlobalDealPageParam(incoming.get("pageSize"));
  if (page !== null) url.searchParams.set("page", String(page));
  if (pageSize !== null) url.searchParams.set("pageSize", String(pageSize));

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const result = upstreamError(
      request,
      response.status,
      body?.message ?? body?.Message ?? "No se pudieron cargar las ofertas"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeGlobalDealCatalog(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
