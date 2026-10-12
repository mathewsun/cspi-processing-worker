-- STAGING ONLY. Apply after the CSPI multi-country migration.
-- A durable coalescing queue of event IDs whose geography may be stale.
CREATE TABLE IF NOT EXISTS news_event_geography_dirty (
  event_id integer PRIMARY KEY REFERENCES "Events"("Id") ON DELETE CASCADE,
  changed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  attempts integer NOT NULL DEFAULT 0,
  last_error text
);
CREATE INDEX IF NOT EXISTS ix_news_event_geography_dirty_changed
  ON news_event_geography_dirty(changed_at,event_id);

CREATE OR REPLACE FUNCTION news_mark_event_geography_dirty()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE eid integer;
BEGIN
  IF TG_TABLE_NAME = 'EventPublications' THEN
    IF TG_OP <> 'INSERT' THEN
      INSERT INTO news_event_geography_dirty(event_id,changed_at)
      SELECT OLD."EventId",clock_timestamp()
      WHERE EXISTS (SELECT 1 FROM "Events" WHERE "Id"=OLD."EventId")
      ON CONFLICT(event_id) DO UPDATE SET changed_at=excluded.changed_at;
    END IF;
    IF TG_OP <> 'DELETE' THEN
      INSERT INTO news_event_geography_dirty(event_id,changed_at)
      SELECT NEW."EventId",clock_timestamp()
      WHERE EXISTS (SELECT 1 FROM "Events" WHERE "Id"=NEW."EventId")
      ON CONFLICT(event_id) DO UPDATE SET changed_at=excluded.changed_at;
    END IF;
  ELSE
    FOR eid IN
      SELECT DISTINCT ep."EventId" FROM "EventPublications" ep
      WHERE ep."PublicationId"=CASE WHEN TG_OP='DELETE' THEN OLD."PublicationId" ELSE NEW."PublicationId" END
    LOOP
      INSERT INTO news_event_geography_dirty(event_id,changed_at)
      VALUES (eid,clock_timestamp())
      ON CONFLICT(event_id) DO UPDATE SET changed_at=excluded.changed_at;
    END LOOP;
  END IF;
  RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS trg_event_membership_geography_dirty ON "EventPublications";
CREATE TRIGGER trg_event_membership_geography_dirty
AFTER INSERT OR UPDATE OR DELETE ON "EventPublications"
FOR EACH ROW EXECUTE FUNCTION news_mark_event_geography_dirty();

DROP TRIGGER IF EXISTS trg_publication_country_geography_dirty ON "PublicationCountries";
CREATE TRIGGER trg_publication_country_geography_dirty
AFTER INSERT OR UPDATE OR DELETE ON "PublicationCountries"
FOR EACH ROW EXECUTE FUNCTION news_mark_event_geography_dirty();

-- Seed existing events once. Production rollout should batch this if very large.
INSERT INTO news_event_geography_dirty(event_id)
SELECT "Id" FROM "Events" ON CONFLICT DO NOTHING;
