import { NextResponse } from "next/server";
import { CSRF_COOKIE_NAME, CSRF_SECURE, CSRF_HEADER_NAME } from "@/lib/security/csrf-config";

export { CSRF_COOKIE_NAME, CSRF_HEADER_NAME };

export function isCsrfEnforced(): boolean {
  const configured = process.env.CSRF_ENFORCE?.trim().toLowerCase();
  if (configured === "true") return true;
  if (configured === "false") return false;
  return process.env.NODE_ENV === "production";
}

export function issueCsrfToken(response: NextResponse, expiresAt: string) {
  const token = crypto.randomUUID().replace(/-/g, "") + crypto.randomUUID().replace(/-/g, "");
  response.cookies.set({
    name: CSRF_COOKIE_NAME,
    value: token,
    httpOnly: false,
    secure: CSRF_SECURE,
    sameSite: "lax",
    path: "/",
    expires: new Date(expiresAt)
  });
}

export function clearCsrfToken(response: NextResponse) {
  response.cookies.set({
    name: CSRF_COOKIE_NAME,
    value: "",
    httpOnly: false,
    secure: CSRF_SECURE,
    sameSite: "lax",
    path: "/",
    expires: new Date(0)
  });
}

export function isMutatingMethod(method: string) {
  return method === "POST" || method === "PUT" || method === "PATCH" || method === "DELETE";
}

export function isTrustedOrigin(request: Request): boolean {
  const origin = request.headers.get("origin");
  const requestUrl = new URL(request.url);

  const trusted = (process.env.CSRF_TRUSTED_ORIGINS ?? "")
    .split(",")
    .map((value) => value.trim().replace(/^['\"]|['\"]$/g, ""))
    .filter(Boolean);

  if (!origin) {
    const referer = request.headers.get("referer");
    if (referer) {
      try {
        return new URL(referer).origin === requestUrl.origin;
      } catch {
        return false;
      }
    }

    const fetchSite = request.headers.get("sec-fetch-site")?.toLowerCase();
    return fetchSite === "same-origin" || fetchSite === "same-site";
  }

  if (trusted.length > 0) {
    const normalizedOrigin = normalizeOrigin(origin);
    const normalizedRequestOrigin = normalizeOrigin(requestUrl.origin);

    if (normalizedOrigin === normalizedRequestOrigin) {
      return true;
    }

    return trusted
      .map((value) => normalizeOrigin(value))
      .includes(normalizedOrigin);
  }

  return origin === requestUrl.origin;
}

function normalizeOrigin(value: string): string {
  try {
    return new URL(value).origin.toLowerCase();
  } catch {
    return value.replace(/\/$/, "").toLowerCase();
  }
}
