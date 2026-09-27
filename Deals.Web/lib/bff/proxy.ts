import { NextResponse } from "next/server";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { type AuthSession } from "@/lib/auth/session";
import { getTraceId, upstreamError } from "@/lib/bff/http";
import { logBff } from "@/lib/bff/observability";

type ProxyJsonOptions = {
  request: Request;
  session: AuthSession;
  url: string;
  init: RequestInit;
  upstreamErrorMessage: string;
};

export async function proxyJsonWithSession(options: ProxyJsonOptions): Promise<{
  response: NextResponse;
  session: AuthSession;
}> {
  const { request, session, url, init, upstreamErrorMessage } = options;
  const startedAt = Date.now();
  const traceId = getTraceId(request);

  const call = await fetchApiWithAutoRefresh(session, url, { ...init, signal: request.signal });
  const upstream = call.response;
  const updatedSession = call.session;

  if (!upstream.ok) {
    await upstream.text().catch(() => "");
    const out = upstreamError(request, upstream.status, upstreamErrorMessage);
    await attachSessionCookie(out, updatedSession, session);

    logBff("warn", {
      event: "proxy_failed",
      traceId,
      route: new URL(request.url).pathname,
      method: request.method,
      durationMs: Date.now() - startedAt,
      status: upstream.status,
      ok: false,
      details: { url }
    });

    return { response: out, session: updatedSession };
  }

  const payload = await upstream.json();
  const out = NextResponse.json(payload, { status: upstream.status });
  await attachSessionCookie(out, updatedSession, session);

  logBff("info", {
    event: "proxy_ok",
    traceId,
    route: new URL(request.url).pathname,
    method: request.method,
    durationMs: Date.now() - startedAt,
    status: upstream.status,
    ok: true,
    details: { url }
  });

  return { response: out, session: updatedSession };
}
