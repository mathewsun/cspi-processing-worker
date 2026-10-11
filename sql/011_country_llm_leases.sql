-- Apply to a staging database after 010_llm_country_jobs.sql.
-- Supports horizontal workers and incremental queue filling.
ALTER TABLE news_country_llm_jobs
  ADD COLUMN IF NOT EXISTS lease_owner text,
  ADD COLUMN IF NOT EXISTS lease_until timestamptz;

CREATE INDEX IF NOT EXISTS ix_news_country_llm_claim
  ON news_country_llm_jobs(next_attempt_at,publication_id)
  WHERE state <> 'Completed';

-- Optional diagnostics
SELECT state, count(*) FROM news_country_llm_jobs GROUP BY state;
SELECT count(*) AS leased FROM news_country_llm_jobs WHERE lease_until > now();
