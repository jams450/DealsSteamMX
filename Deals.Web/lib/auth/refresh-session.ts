import { decodeJwt } from "jose";
import { getApiBaseUrl } from "@/lib/api/config";
import type { AuthSession } from "@/lib/auth/session";
import { coordinateRefresh } from "@/lib/auth/refresh-coordinator";

type ApiRefreshResponse = {
  token: string;
  expiration: string;
  username: string;
  refreshToken: string;
  refreshTokenExpiration: string;
};

// Independent timeout: one caller aborting must not cancel the shared rotation.
export async function refreshSession(session: AuthSession): Promise<AuthSession | null> {
  if (!session.refreshToken) return null;
  return coordinateRefresh(session.refreshToken, async () => {
    const response = await fetch(`${getApiBaseUrl()}/api/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: session.refreshToken }),
      cache: "no-store",
      signal: AbortSignal.timeout(30_000)
    });
    if (!response.ok) {
      console.warn("[bff.auth.refresh_upstream_failed]", { status: response.status });
      return null;
    }
    const data = await response.json() as ApiRefreshResponse;
    if (!data.token || !data.refreshToken || !Number.isFinite(Date.parse(data.expiration)) ||
        !Number.isFinite(Date.parse(data.refreshTokenExpiration))) return null;
    const claims = decodeJwt(data.token);
    const id = Number(claims.sub ?? claims["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"]);
    if (!Number.isInteger(id) || id <= 0 || id !== session.user.id) return null;
    const role = claims["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];
    return {
      accessToken: data.token,
      expiresAt: data.expiration,
      refreshToken: data.refreshToken,
      refreshExpiresAt: data.refreshTokenExpiration,
      user: { id, username: data.username, role: typeof role === "string" ? role : undefined }
    };
  });
}
