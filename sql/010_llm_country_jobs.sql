-- Staging only: durable per-model classification state.
CREATE TABLE IF NOT EXISTS news_country_llm_jobs (
    publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    model_id varchar(120) NOT NULL,
    state varchar(16) NOT NULL DEFAULT 'Pending'
      CHECK(state IN ('Pending','Completed','Failed')),
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error text,
    processed_at timestamptz,
    PRIMARY KEY(publication_id,model_id)
);
CREATE INDEX IF NOT EXISTS ix_news_country_llm_jobs_due
  ON news_country_llm_jobs(next_attempt_at) WHERE state <> 'Completed';

-- Explicitly queue a limited test batch; the worker will not automatically
-- process every existing publication on startup.
-- INSERT INTO news_country_llm_jobs(publication_id,model_id)
-- SELECT "Id", 'your-model-version' FROM "Publications"
-- ORDER BY "Id" DESC LIMIT 100 ON CONFLICT DO NOTHING;
