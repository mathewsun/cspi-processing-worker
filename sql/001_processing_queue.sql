-- Development/staging migration, not yet wired to EF migrations.
-- Publication IDs refer to the CSPI Publications table.
CREATE TABLE IF NOT EXISTS news_processing_jobs (
    publication_id integer PRIMARY KEY REFERENCES "Publications"("Id") ON DELETE CASCADE,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    lease_owner text,
    lease_until timestamptz,
    attempts integer NOT NULL DEFAULT 0,
    last_error text,
    processed_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_news_processing_jobs_due
    ON news_processing_jobs (next_attempt_at)
    WHERE processed_at IS NULL;

CREATE TABLE IF NOT EXISTS news_article_fingerprints (
    publication_id integer PRIMARY KEY REFERENCES "Publications"("Id") ON DELETE CASCADE,
    url_hash text NOT NULL,
    text_hash text,
    normalized_title text NOT NULL,
    exact_duplicate_of integer REFERENCES "Publications"("Id"),
    processed_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_news_article_fingerprints_url_hash
    ON news_article_fingerprints (url_hash);
CREATE INDEX IF NOT EXISTS ix_news_article_fingerprints_text_hash
    ON news_article_fingerprints (text_hash) WHERE text_hash IS NOT NULL;

-- Explicit/manual queue initialization for staging only.
INSERT INTO news_processing_jobs (publication_id)
SELECT "Id" FROM "Publications"
ON CONFLICT DO NOTHING;
