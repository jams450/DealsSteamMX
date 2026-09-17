-- Daily USD/MXN (and any future pair) rate snapshot used to derive approximate prices.
-- One row per (base, quote, rate_date); upserted by the FX refresh job, never written per user request.

CREATE TABLE IF NOT EXISTS public.fx_rates (
    base VARCHAR(3) NOT NULL,
    quote VARCHAR(3) NOT NULL,
    rate NUMERIC(18,8) NOT NULL,
    rate_date DATE NOT NULL,
    source VARCHAR(32) NOT NULL,
    fetched_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (base, quote, rate_date)
);

ANALYZE public.fx_rates;
