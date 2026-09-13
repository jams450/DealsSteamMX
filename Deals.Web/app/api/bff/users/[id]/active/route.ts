import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { badRequest, forbidden, unauthorized } from "@/lib/bff/http";

function isAdmin(role?: string) {
  return (role ?? "").toLowerCase() === "admin";
}

function parseId(value: string) {
  const id = Number(value);
  return Number.isInteger(id) && id > 0 ? id : null;
}

export async function PATCH(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const session = await getServerSession();
  if (!session) return unauthorized();
  if (!isAdmin(session.user.role)) return forbidden();
  let authSession = session;

  const { id: idRaw } = await params;
  const id = parseId(idRaw);
  if (!id) return badRequest(undefined, "Id inválido");

  const body = (await request.json().catch(() => null)) as { active?: unknown } | null;
  if (!body || typeof body.active !== "boolean") {
    return badRequest(undefined, "Payload inválido");
  }

  const call = await fetchApiWithAutoRefresh(authSession, `${getApiBaseUrl()}/api/users/${id}/active`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body.active)
  });
  const response = call.response;
  authSession = call.session;

  const raw = await response.text();
  const out = new NextResponse(raw, {
    status: response.status,
    headers: {
      "content-type": response.headers.get("content-type") ?? "application/json"
    }
  });
  await attachSessionCookie(out, authSession, session);
  return out;
}
