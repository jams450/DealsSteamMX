import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeJobs } from "@/lib/contracts/jobs";

// Solo se reenvían los filtros que el backend conoce y con un tamaño acotado: la basura se descarta y el
// backend aplica su default. `job`/`status`/`trigger` son claves de máquina, no texto libre.
const MAX_FILTER_LENGTH = 64;

function filter(value: string | null): string | null {
  const trimmed = value?.trim();
  return trimmed && trimmed.length <= MAX_FILTER_LENGTH ? trimmed : null;
}

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if ((session.user.role ?? "").toLowerCase() !== "admin") return forbidden(request);

  const incoming = new URL(request.url).searchParams;
  const url = new URL("/api/jobs", getApiBaseUrl());
  const page = Number(incoming.get("page") ?? "1");
  const pageSize = Number(incoming.get("pageSize") ?? "25");
  url.searchParams.set("page", String(Number.isInteger(page) && page > 0 ? page : 1));
  url.searchParams.set("pageSize", String(Number.isInteger(pageSize) && pageSize > 0 ? pageSize : 25));
  for (const key of ["job", "status", "trigger"] as const) {
    const value = filter(incoming.get(key));
    if (value) url.searchParams.set(key, value);
  }

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, url.toString(), {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo cargar el historial de jobs");
    await attachSessionCookie(result, updatedSession, session);
    return result;
  }

  const result = NextResponse.json(normalizeJobs(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
