import { NextResponse } from "next/server";
import { getApiBaseUrl } from "@/lib/api/config";
import { attachSessionCookie, fetchApiWithAutoRefresh } from "@/lib/auth/api-session";
import { getServerSession } from "@/lib/auth/session";
import { forbidden, unauthorized, upstreamError } from "@/lib/bff/http";
import { normalizeJobs } from "@/lib/contracts/jobs";

export async function GET(request: Request) {
  const session = await getServerSession();
  if (!session) return unauthorized();
  if ((session.user.role ?? "").toLowerCase() !== "admin") return forbidden();
  const url = new URL(request.url);
  const target = `${getApiBaseUrl()}/api/jobs?page=${url.searchParams.get("page") ?? "1"}&pageSize=${url.searchParams.get("pageSize") ?? "25"}`;
  const { response, session: updatedSession } = await fetchApiWithAutoRefresh(session, target, { cache: "no-store" });
  if (!response.ok) return upstreamError(undefined, response.status, "Failed to load jobs");
  const result = NextResponse.json(normalizeJobs(await response.json()));
  await attachSessionCookie(result, updatedSession, session);
  return result;
}
