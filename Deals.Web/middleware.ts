import type { NextRequest, NextResponse as NextResponseType } from "next/server";
import { NextResponse } from "next/server";
import { decryptSession, isSessionUsable, SESSION_COOKIE_NAME } from "@/lib/auth/session";
import { csrfRejected, unauthorized } from "@/lib/bff/http";
import { CSRF_COOKIE_NAME, CSRF_HEADER_NAME, isCsrfEnforced, isMutatingMethod, isTrustedOrigin } from "@/lib/security/csrf";
import { isPublicRoute } from "@/lib/security/route-policy";

function clearSessionCookies(response: NextResponseType) {
  response.cookies.delete(SESSION_COOKIE_NAME);
  response.cookies.delete(CSRF_COOKIE_NAME);
  return response;
}

function redirectToLogin(request: NextRequest) {
  const url = new URL("/login", request.url);
  url.searchParams.set("reason", "session_expired");
  return clearSessionCookies(NextResponse.redirect(url));
}

function requiresCsrf(pathname: string) {
  return pathname.startsWith("/api/bff/") || pathname === "/api/auth/logout" || pathname === "/api/auth/refresh";
}

export async function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;

  if (isCsrfEnforced() && requiresCsrf(pathname) && isMutatingMethod(request.method)) {
    const cookieToken = request.cookies.get(CSRF_COOKIE_NAME)?.value;
    const headerToken = request.headers.get(CSRF_HEADER_NAME);

    if (!isTrustedOrigin(request) || !cookieToken || !headerToken || cookieToken !== headerToken) {
      return csrfRejected(request);
    }
  }

  if (isPublicRoute(pathname)) {
    return NextResponse.next();
  }

  const rawCookie = request.cookies.get(SESSION_COOKIE_NAME)?.value;
  const session = rawCookie ? await decryptSession(rawCookie) : null;
  const hasUsableSession = Boolean(session && isSessionUsable(session));

  if (pathname === "/login") {
    if (request.nextUrl.searchParams.get("reason") === "session_expired") {
      return clearSessionCookies(NextResponse.next());
    }

    if (hasUsableSession) {
      return NextResponse.redirect(new URL("/", request.url));
    }

    return rawCookie ? clearSessionCookies(NextResponse.next()) : NextResponse.next();
  }

  if (hasUsableSession) {
    return NextResponse.next();
  }

  if (pathname.startsWith("/api/")) {
    return clearSessionCookies(unauthorized(request, "No active session"));
  }

  return redirectToLogin(request);
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico|.*\\.(?:css|js|map|txt|xml|ico|png|jpg|jpeg|gif|svg|webp|avif|woff|woff2|ttf|eot)$).*)"]
};
