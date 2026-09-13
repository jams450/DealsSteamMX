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

  const call = await fetchApiWithAutoRefresh(session, url, init);
  const upstream = call.response;
  const updatedSession = call.session;

  if (!upstream.ok) {
    const body = (await upstream.json().catch(() => null)) as { message?: string; Message?: string } | null;
    const out = upstreamError(request, upstream.status, body?.message ?? body?.Message ?? upstreamErrorMessage);
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
