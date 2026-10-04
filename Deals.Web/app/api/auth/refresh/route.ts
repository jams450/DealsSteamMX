import { refreshSession } from "@/lib/auth/refresh-session";
import { RefreshCapacityError } from "@/lib/auth/refresh-coordinator";
import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { decryptSession, encryptSession, SESSION_COOKIE_NAME, SESSION_COOKIE_SECURE } from "@/lib/auth/session";
import { sessionExpired, unauthorized } from "@/lib/bff/http";
import { issueCsrfToken } from "@/lib/security/csrf";

export async function POST() {
  const traceId = crypto.randomUUID();
  const cookieStore = await cookies();
  const encrypted = cookieStore.get(SESSION_COOKIE_NAME)?.value;
  if (!encrypted) {
    return unauthorized(traceId, "No active session");
  }

  const session = await decryptSession(encrypted);
  if (!session?.refreshToken) {
    return unauthorized(traceId, "Session has no refresh token");
  }

  console.info("[bff.auth.manual_refresh_attempt]", { traceId });

  let updatedSession;
  try {
    updatedSession = await refreshSession(session);
  } catch (error) {
    if (!(error instanceof RefreshCapacityError)) throw error;
    return NextResponse.json({ code: "UPSTREAM_ERROR", message: "Refresh temporarily unavailable", traceId }, { status: 503 });
  }
  if (!updatedSession) return sessionExpired(traceId, "Unable to refresh session");
  const sessionToken = await encryptSession(updatedSession);
  const response = NextResponse.json({ user: updatedSession.user, traceId });
  response.cookies.set({
    name: SESSION_COOKIE_NAME,
    value: sessionToken,
    httpOnly: true,
    secure: SESSION_COOKIE_SECURE,
    sameSite: "lax",
    path: "/",
    expires: new Date(updatedSession.refreshExpiresAt ?? updatedSession.expiresAt)
  });
  issueCsrfToken(response, updatedSession.refreshExpiresAt ?? updatedSession.expiresAt);
  console.info("[bff.auth.manual_refresh_succeeded]", { traceId, userId: updatedSession.user.id });

  return response;
}
