import { NextResponse } from "next/server";
import { cookies } from "next/headers";
import { getApiBaseUrl } from "@/lib/api/config";
import { decryptSession, SESSION_COOKIE_NAME, SESSION_COOKIE_SECURE } from "@/lib/auth/session";
import { getTraceId } from "@/lib/bff/http";
import { clearCsrfToken } from "@/lib/security/csrf";

export async function POST(request: Request) {
  const traceId = getTraceId(request);
  const cookieStore = await cookies();
  const encrypted = cookieStore.get(SESSION_COOKIE_NAME)?.value;

  if (encrypted) {
    const session = await decryptSession(encrypted);
    if (session?.refreshToken) {
      const headers: Record<string, string> = { "Content-Type": "application/json" };
      if (session.accessToken) {
        headers.Authorization = `Bearer ${session.accessToken}`;
      }
      await fetch(`${getApiBaseUrl()}/api/auth/logout`, {
        method: "POST",
        headers,
        body: JSON.stringify({ refreshToken: session.refreshToken }),
        cache: "no-store"
      }).catch(() => null);
    }
  }

  const response = NextResponse.json({ ok: true, code: "LOGGED_OUT", traceId });
  response.cookies.set({
    name: SESSION_COOKIE_NAME,
    value: "",
    path: "/",
    expires: new Date(0),
    httpOnly: true,
    sameSite: "lax",
    secure: SESSION_COOKIE_SECURE
  });
  clearCsrfToken(response);
  console.info("[bff.auth.logout]", { traceId });

  return response;
}
