import { NextResponse } from "next/server";

type ErrorCode = "UNAUTHORIZED" | "SESSION_EXPIRED" | "FORBIDDEN" | "BAD_REQUEST" | "UPSTREAM_ERROR" | "CSRF_REJECTED";

type ErrorPayload = {
  code: ErrorCode;
  message: string;
  traceId: string;
};

export function getTraceId(request?: Request): string {
  const fromHeader = request?.headers.get("x-request-id") ?? request?.headers.get("x-correlation-id");
  return fromHeader?.trim() || crypto.randomUUID();
}

function resolveTraceId(requestOrTraceId?: Request | string): string {
  if (typeof requestOrTraceId === "string") {
    const traceId = requestOrTraceId.trim();
    return traceId.length > 0 ? traceId : crypto.randomUUID();
  }

  return getTraceId(requestOrTraceId);
}

export function errorResponse(status: number, payload: ErrorPayload) {
  return NextResponse.json(payload, { status });
}

export function unauthorized(requestOrTraceId?: Request | string, message = "Unauthorized") {
  return errorResponse(401, { code: "UNAUTHORIZED", message, traceId: resolveTraceId(requestOrTraceId) });
}

export function sessionExpired(requestOrTraceId?: Request | string, message = "Session expired") {
  return errorResponse(401, { code: "SESSION_EXPIRED", message, traceId: resolveTraceId(requestOrTraceId) });
}

export function forbidden(requestOrTraceId?: Request | string, message = "Forbidden") {
  return errorResponse(403, { code: "FORBIDDEN", message, traceId: resolveTraceId(requestOrTraceId) });
}

export function csrfRejected(requestOrTraceId?: Request | string, message = "CSRF validation failed") {
  return errorResponse(403, { code: "CSRF_REJECTED", message, traceId: resolveTraceId(requestOrTraceId) });
}

export function badRequest(requestOrTraceId: Request | string | undefined, message: string) {
  return errorResponse(400, { code: "BAD_REQUEST", message, traceId: resolveTraceId(requestOrTraceId) });
}

export function upstreamError(requestOrTraceId: Request | string | undefined, status: number, message: string) {
  const code: ErrorCode = status === 401 ? "SESSION_EXPIRED" : status === 403 ? "FORBIDDEN" : "UPSTREAM_ERROR";
  return errorResponse(status, { code, message, traceId: resolveTraceId(requestOrTraceId) });
}
