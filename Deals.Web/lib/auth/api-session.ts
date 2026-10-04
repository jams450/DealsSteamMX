import { refreshSession } from "@/lib/auth/refresh-session";
import { RefreshCapacityError } from "@/lib/auth/refresh-coordinator";
import { NextResponse } from "next/server";
import { type AuthSession, encryptSession, SESSION_COOKIE_NAME, SESSION_COOKIE_SECURE } from "@/lib/auth/session";

export async function fetchApiWithAutoRefresh(
  session: AuthSession,
  input: string,
  init: RequestInit,
  signal?: AbortSignal,
  timeoutMs = 30_000
): Promise<{ response: Response; session: AuthSession }> {
  const requestSignal = () => {
    const timeout = AbortSignal.timeout(timeoutMs);
    return signal ? AbortSignal.any([signal, timeout]) : timeout;
  };
  const execute = (accessToken: string) =>
    fetch(input, {
      ...init,
      signal: requestSignal(),
      headers: {
        ...(init.headers ?? {}),
        Authorization: `Bearer ${accessToken}`,
        "Content-Type": "application/json"
      }
    });

  const first = await execute(session.accessToken);
  if (first.status !== 401 || !session.refreshToken) {
    return { response: first, session };
  }

  console.info("[bff.auth.refresh_attempt]", { path: input });

  let updatedSession;
  try {
    updatedSession = await refreshSession(session);
  } catch (error) {
    if (!(error instanceof RefreshCapacityError)) throw error;
    return { response: new Response(null, { status: 503 }), session };
  }
  if (!updatedSession) {
    console.warn("[bff.auth.refresh_failed]", { path: input });
    return { response: first, session };
  }

  const retry = await execute(updatedSession.accessToken);
  console.info("[bff.auth.refresh_succeeded]", { path: input, retryStatus: retry.status });
  return { response: retry, session: updatedSession };
}

export async function attachSessionCookie(response: NextResponse, session: AuthSession, original: AuthSession) {
  if (
    session.accessToken === original.accessToken &&
    session.expiresAt === original.expiresAt &&
    session.refreshToken === original.refreshToken &&
    session.refreshExpiresAt === original.refreshExpiresAt
  ) {
    return;
  }

  const encrypted = await encryptSession(session);
  response.cookies.set({
    name: SESSION_COOKIE_NAME,
    value: encrypted,
    httpOnly: true,
    secure: SESSION_COOKIE_SECURE,
    sameSite: "lax",
    path: "/",
    expires: new Date(session.refreshExpiresAt ?? session.expiresAt)
  });
  console.info("[bff.auth.cookie_rotated]", { userId: session.user.id });
}
