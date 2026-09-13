import { decodeJwt } from "jose";
import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { type AuthSession, encryptSession, SESSION_COOKIE_NAME, SESSION_COOKIE_SECURE } from "@/lib/auth/session";

type ApiRefreshResponse = {
  token: string;
  expiration: string;
  username: string;
  refreshToken?: string;
  refreshTokenExpiration?: string;
};

export async function fetchApiWithAutoRefresh(
  session: AuthSession,
  input: string,
  init: RequestInit
): Promise<{ response: Response; session: AuthSession }> {
  const execute = (accessToken: string) =>
    fetch(input, {
      ...init,
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

  const refreshRes = await fetch(`${getApiBaseUrl()}/api/auth/refresh`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ refreshToken: session.refreshToken }),
    cache: "no-store"
  });

  if (!refreshRes.ok) {
    console.warn("[bff.auth.refresh_failed]", { path: input, status: refreshRes.status });
    return { response: first, session };
  }

  const refreshed = (await refreshRes.json()) as ApiRefreshResponse;
  const claims = decodeJwt(refreshed.token);
  const roleClaim = claims["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];
  const role = typeof roleClaim === "string" ? roleClaim : session.user.role;

  const updatedSession: AuthSession = {
    accessToken: refreshed.token,
    expiresAt: refreshed.expiration,
    refreshToken: refreshed.refreshToken,
    refreshExpiresAt: refreshed.refreshTokenExpiration,
    user: {
      ...session.user,
      username: refreshed.username,
      role
    }
  };

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
