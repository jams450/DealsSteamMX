import { CSRF_COOKIE_NAME, CSRF_HEADER_NAME } from "@/lib/security/csrf-config";
import { redirectToLoginOnSessionExpired } from "@/lib/bff/client-session";

function readCookie(name: string): string | null {
  if (typeof document === "undefined") return null;
  const target = `${encodeURIComponent(name)}=`;
  for (const part of document.cookie.split(";")) {
    const value = part.trim();
    if (value.startsWith(target)) {
      return decodeURIComponent(value.slice(target.length));
    }
  }
  return null;
}

export async function csrfFetch(input: RequestInfo | URL, init: RequestInit = {}) {
  const method = (init.method ?? "GET").toUpperCase();
  const headers = new Headers(init.headers ?? {});

  if (method === "POST" || method === "PUT" || method === "PATCH" || method === "DELETE") {
    const token = readCookie(CSRF_COOKIE_NAME);
    if (token) {
      headers.set(CSRF_HEADER_NAME, token);
    }
  }

  const response = await fetch(input, {
    ...init,
    headers
  });

  redirectToLoginOnSessionExpired(response);
  return response;
}
