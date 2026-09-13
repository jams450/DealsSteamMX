import { decodeJwt } from "jose";
import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { encryptSession, SESSION_COOKIE_NAME, SESSION_COOKIE_SECURE } from "@/lib/auth/session";
import { badRequest, getTraceId, unauthorized, upstreamError } from "@/lib/bff/http";
import { issueCsrfToken } from "@/lib/security/csrf";

type LoginRequest = {
  username: string;
  password: string;
};

type ApiLoginResponse = {
  token: string;
  expiration: string;
  username: string;
  refreshToken?: string;
  refreshTokenExpiration?: string;
};

export async function POST(request: Request) {
  const traceId = getTraceId(request);
  const body = (await request.json()) as LoginRequest;

  if (!body.username || !body.password) {
    return badRequest(request, "Username and password are required");
  }

  const apiResponse = await fetch(`${getApiBaseUrl()}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username: body.username, password: body.password }),
    cache: "no-store"
  });

  if (!apiResponse.ok) {
    return upstreamError(request, apiResponse.status, "Invalid credentials");
  }

  const loginData = (await apiResponse.json()) as ApiLoginResponse;
  const claims = decodeJwt(loginData.token);
  const idRaw =
    claims.sub ??
    claims["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"];
  const userId = Number(idRaw);
  if (!idRaw || Number.isNaN(userId)) {
    return unauthorized(request, "Token does not include a valid user id claim");
  }

  const role = String(claims["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"]);

  const sessionToken = await encryptSession({
    accessToken: loginData.token,
    expiresAt: loginData.expiration,
    refreshToken: loginData.refreshToken,
    refreshExpiresAt: loginData.refreshTokenExpiration,
    user: {
      id: userId,
      username: loginData.username,
      role
    }
  });

  const response = NextResponse.json({ user: { id: userId, username: loginData.username, role }, traceId });
  response.cookies.set({
    name: SESSION_COOKIE_NAME,
    value: sessionToken,
    httpOnly: true,
    secure: SESSION_COOKIE_SECURE,
    sameSite: "lax",
    path: "/",
    expires: new Date(loginData.refreshTokenExpiration ?? loginData.expiration)
  });
  issueCsrfToken(response, loginData.refreshTokenExpiration ?? loginData.expiration);

  console.info("[bff.auth.login]", { traceId, userId });

  return response;
}
