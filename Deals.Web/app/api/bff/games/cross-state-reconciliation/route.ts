import { NextResponse } from "next/server";
import { normalizeCrossStateCandidates } from "@/lib/contracts/games-merge";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized(request);
  if ((session.user.role ?? "").toLowerCase() !== "admin") return forbidden(request);
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/games/cross-state-reconciliation`, { method: "GET", cache: "no-store" });
  if (!response.ok) {
    const result = upstreamError(request, response.status, "No se pudo cargar la reconciliación");
    await attachSessionCookie(result, updatedSession, session); return result;
  }
  const candidates = normalizeCrossStateCandidates(await response.json().catch(() => null));
  if (candidates === null) {
    const result = upstreamError(request, 502, "El servidor devolvió una reconciliación inválida");
    await attachSessionCookie(result, updatedSession, session); return result;
  }
  const result = NextResponse.json(candidates, { headers: { "cache-control": "no-store" } });
  await attachSessionCookie(result, updatedSession, session); return result;
}
