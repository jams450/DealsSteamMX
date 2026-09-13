import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeUsers } from "@/lib/contracts/users-admin";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

export async function GET() {
  const session = await getServerSession();
  if (!session) return unauthorized();
  if (!isAdmin(session.user.role)) return forbidden();

  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/users`, {
    method: "GET",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { message?: string; Message?: string } | null;
    return upstreamError(undefined, response.status, body?.message ?? body?.Message ?? "Failed to load users");
  }

  const raw = await response.json();
  const result = NextResponse.json(normalizeUsers(raw));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}

export async function POST(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized();
  if (!isAdmin(session.user.role)) return forbidden();

  const body = await request.json();
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, `${getApiBaseUrl()}/api/users`, {
    method: "POST",
    body: JSON.stringify(body)
  });

  const raw = await response.text();
  const result = new NextResponse(raw, {
    status: response.status,
    headers: {
      "content-type": response.headers.get("content-type") ?? "application/json"
    }
  });
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
