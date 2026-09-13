type ApiErrorBody = { code?: string; message?: string; Message?: string } | null;

const SESSION_EXPIRED_CODES = new Set(["SESSION_EXPIRED", "UNAUTHORIZED"]);

export function redirectToLoginOnSessionExpired(response: Response, body?: ApiErrorBody): boolean {
  if (typeof window === "undefined") {
    return false;
  }

  if (response.status !== 401) {
    return false;
  }

  const code = body?.code?.toUpperCase();
  if (!code || SESSION_EXPIRED_CODES.has(code)) {
    window.location.replace("/login?reason=session_expired");
    return true;
  }

  return false;
}

export async function parseApiError(response: Response, fallback: string): Promise<Error> {
  const body = (await response.json().catch(() => null)) as ApiErrorBody;
  redirectToLoginOnSessionExpired(response, body);
  return new Error(body?.message ?? body?.Message ?? fallback);
}
