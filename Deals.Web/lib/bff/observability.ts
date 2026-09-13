type BffLogLevel = "info" | "warn" | "error";

type BffRequestLog = {
  event: string;
  traceId: string;
  route: string;
  method: string;
  durationMs?: number;
  status?: number;
  ok?: boolean;
  details?: Record<string, unknown>;
};

export function logBff(level: BffLogLevel, payload: BffRequestLog) {
  const logger = level === "warn" ? console.warn : level === "error" ? console.error : console.info;
  logger("[bff.request]", payload);
}
