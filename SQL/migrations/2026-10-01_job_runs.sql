-- Execution log for the periodic HostedServices. One row per cycle, whatever its outcome, so the jobs can
-- answer "did I already run?" instead of replaying a full pass on every process start.
--
-- trigger: 'startup'  -> recovery pass right after the host started
--          'scheduled'-> the run of the configured daily slot
--          'manual'   -> an admin-triggered run; excluded from the "already ran" window, because it never
--                        performs the expensive price pass.
-- status:  'running' | 'ok' | 'failed'. A row left in 'running' means the process died mid-cycle: it is
--          honest (the work was interrupted) and still counts for the window.
-- details: JSONB counters of the cycle. No secrets: counters and statuses only.

CREATE TABLE IF NOT EXISTS public.job_runs (
    job_run_id  BIGSERIAL PRIMARY KEY,
    job         VARCHAR(64) NOT NULL,
    trigger     VARCHAR(16) NOT NULL,
    status      VARCHAR(16) NOT NULL,
    started_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    finished_at TIMESTAMPTZ NULL,
    details     JSONB NULL
);

CREATE INDEX IF NOT EXISTS idx_job_runs_job_started ON public.job_runs(job, started_at DESC);

ANALYZE public.job_runs;
