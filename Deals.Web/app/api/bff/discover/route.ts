import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, unauthorized, upstreamError } from "@/lib/bff/http";
import {
  DISCOVER_CATEGORIES,
  normalizeDiscoverPage,
  parseDiscoverCategoryParam,
  parseDiscoverPageParam
} from "@/lib/contracts/discover";

// BFF del catálogo de descubrimiento (`GET /api/catalogdeals`). Solo lectura y sin llamadas a proveedores:
// reenvía la categoría y los topes de paginación al backend, que responde con la página seleccionada y los
// tres contadores de pestaña en un único sobre. El usuario nunca viaja en la query: el backend lo deriva del
// token y `/discover` es el catálogo global, no el de una cuenta.
export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);

  const params = new URL(request.url).searchParams;
  const rawCategory = params.get("category");
  // Sin categoría se usa la primera pestaña; una categoría desconocida es un 400 explícito, nunca un
  // silencioso cambio de pestaña.
  const category = rawCategory === null || rawCategory.trim() === ""
    ? "under50"
    : parseDiscoverCategoryParam(rawCategory);
  if (category === null) {
    return badRequest(
      request,
      `Categoría desconocida: usa ${DISCOVER_CATEGORIES.join(", ")}`
    );
  }

  const url = new URL("/api/catalogdeals", getApiBaseUrl());
  url.searchParams.set("category", category);
  // Los enteros inválidos se descartan para que el backend aplique su default en vez de recibir basura.
  const page = parseDiscoverPageParam(params.get("page"));
  const pageSize = parseDiscoverPageParam(params.get("pageSize"));
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
      body?.message ?? body?.Message ?? "No se pudo cargar el descubrimiento"
    );
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeDiscoverPage(await response.json(), category));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
