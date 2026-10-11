-- Apply after the news collector and processing queue schemas in a TEST database.
-- Durable, idempotent mapping from collector staging items to CSPI Publications.
CREATE TABLE IF NOT EXISTS news_staging_publications (
    staging_item_id bigint PRIMARY KEY REFERENCES news_collected_items(id) ON DELETE CASCADE,
    publication_id integer NOT NULL UNIQUE REFERENCES "Publications"("Id") ON DELETE CASCADE,
    promoted_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_news_staging_publications_promoted
    ON news_staging_publications (promoted_at DESC);

-- Diagnostic: pending promotions
SELECT count(*) AS unpromoted
FROM news_collected_items i
WHERE NOT EXISTS (
    SELECT 1 FROM news_staging_publications m WHERE m.staging_item_id = i.id
);
