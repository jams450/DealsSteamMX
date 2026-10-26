import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import {
  CATALOG_FRESHNESS_FILTERS,
  normalizeCatalogFreshnessPage,
  parseCatalogFreshnessFilterParam,
  parseCatalogFreshnessPageParam
} from "@/lib/contracts/catalog-freshness";
import { readUpstreamJson } from "./read-upstream-body";

// BFF del panel «Estado de actualización de precios» de la lista de deseos (`GET /api/catalogdeals/freshness`,
// spec S4/S7). Solo lectura: reenvía el filtro y los topes de paginación al backend, que responde con la página
// del filtro seleccionado y los cuatro contadores de ESA lista en el mismo sobre.
//
// No hay ruta de escritura. El refresco manual de precios se lanza desde `/jobs` (job `catalog.prices-refresh`),
// no desde `/discover`: esta página jamás inicia un job, toca un proveedor ni modifica una fecha. El usuario
// nunca viaja en la query: el backend lo deriva del token y el alcance es la lista de deseos de ese usuario.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const params = new URL(request.url).searchParams;
  const rawStatus = params.get("status");
  // Sin filtro se usa la lista de deseos completa; un filtro desconocido es un 400 explícito, nunca un
  // silencioso cambio de pestaña (mismo criterio que `/api/bff/discover`).
  const filter = rawStatus === null || rawStatus.trim() === ""
    ? "all"
    : parseCatalogFreshnessFilterParam(rawStatus);
  if (filter === null) {
    return badRequest(
      request,
      `Estado de actualización desconocido: usa ${CATALOG_FRESHNESS_FILTERS.join(", ")}`
    );
  }

  const url = new URL("/api/catalogdeals/freshness", getApiBaseUrl());
  url.searchParams.set("status", filter);
  // Los enteros inválidos se descartan para que el backend aplique su default en vez de recibir basura.
  const page = parseCatalogFreshnessPageParam(params.get("page"));
  const pageSize = parseCatalogFreshnessPageParam(params.get("pageSize"));
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
      body?.message ??
        body?.Message ??
        "No se pudo cargar el estado de actualización de precios de tu lista de deseos"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  // El filtro pedido es la única fuente de la etiqueta: una respuesta atrasada no puede renombrar la pestaña.
  // El cuerpo se lee de forma guardada: un 2xx ilegible (HTML de un proxy, cuerpo truncado) es un error
  // upstream tipado con su traceId y conserva la sesión rotada, nunca un 500 sin tipo que la descarte.
  const body = await readUpstreamJson(response);
  if (!body.ok) {
    const result = upstreamError(
      request,
      502,
      "No se pudo leer el estado de actualización de precios: el servidor respondió con un cuerpo ilegible"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeCatalogFreshnessPage(body.body, filter));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
