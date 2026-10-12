-- STAGING ONLY. Apply after processing SQL 009 and CSPI multi-country migration.
-- Coalesced, durable queue for changed country-role proposals.
CREATE TABLE IF NOT EXISTS news_publication_geography_dirty (
    publication_id integer PRIMARY KEY REFERENCES "Publications"("Id") ON DELETE CASCADE,
    changed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_news_publication_geography_dirty_due
    ON news_publication_geography_dirty(changed_at,publication_id);

CREATE OR REPLACE FUNCTION news_mark_publication_geography_dirty()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        INSERT INTO news_publication_geography_dirty(publication_id,changed_at)
        SELECT OLD.publication_id,clock_timestamp()
        WHERE EXISTS(SELECT 1 FROM "Publications" WHERE "Id"=OLD.publication_id)
        ON CONFLICT(publication_id) DO UPDATE SET changed_at=excluded.changed_at;
    END IF;
    IF TG_OP <> 'DELETE' THEN
        INSERT INTO news_publication_geography_dirty(publication_id,changed_at)
        SELECT NEW.publication_id,clock_timestamp()
        WHERE EXISTS(SELECT 1 FROM "Publications" WHERE "Id"=NEW.publication_id)
        ON CONFLICT(publication_id) DO UPDATE SET changed_at=excluded.changed_at;
    END IF;
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS trg_news_country_proposal_dirty ON news_country_role_proposals;
CREATE TRIGGER trg_news_country_proposal_dirty
AFTER INSERT OR UPDATE OR DELETE ON news_country_role_proposals
FOR EACH ROW EXECUTE FUNCTION news_mark_publication_geography_dirty();

-- Initial backfill. For very large databases, perform this in smaller batches.
INSERT INTO news_publication_geography_dirty(publication_id)
SELECT DISTINCT publication_id FROM news_country_role_proposals
ON CONFLICT DO NOTHING;
