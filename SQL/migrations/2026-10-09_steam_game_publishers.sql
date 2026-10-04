-- Steam appdetails publisher metadata. Manual migration; apply before deploying the API.
-- No default/backfill: NULL keeps legacy rows unknown until normal or forced Steam refresh.
-- A fetched snapshot without publisher names stores an empty array; neither value forces a retry.
ALTER TABLE steam_games ADD COLUMN IF NOT EXISTS publishers TEXT[];
