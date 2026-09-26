export type JobRun = {
  jobRunId: number; job: string; trigger: string; status: string;
  startedAt: string; finishedAt: string | null; durationMilliseconds: number | null;
  details: Record<string, unknown> | null;
};
export type JobsResponse = { summary: { total: number; running: number; ok: number; failed: number; lastStartedAt: string | null }; items: JobRun[]; page: number; pageSize: number; totalPages: number };

export function normalizeJobs(input: unknown): JobsResponse {
  const value = (input && typeof input === "object" ? input : {}) as Record<string, unknown>;
  const summaryValue = (value.summary && typeof value.summary === "object" ? value.summary : {}) as Record<string, unknown>;
  const items = Array.isArray(value.items) ? value.items.filter((item): item is JobRun => Boolean(item)) : [];
  return { summary: { total: Number(summaryValue.total ?? 0), running: Number(summaryValue.running ?? 0), ok: Number(summaryValue.ok ?? 0), failed: Number(summaryValue.failed ?? 0), lastStartedAt: typeof summaryValue.lastStartedAt === "string" ? summaryValue.lastStartedAt : null }, items, page: Number(value.page ?? 1), pageSize: Number(value.pageSize ?? 25), totalPages: Number(value.totalPages ?? 1) };
}
