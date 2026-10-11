-- Test database only. Requires CSPI EventPublications migration and review table 006.
-- Tracks approved pair materialization idempotently, without altering review decisions.
CREATE TABLE IF NOT EXISTS news_event_review_applications (
  publication_id_a integer NOT NULL,
  publication_id_b integer NOT NULL,
  event_id integer REFERENCES "Events"("Id") ON DELETE SET NULL,
  outcome varchar(20) NOT NULL CHECK (outcome IN ('Linked','Conflict')),
  detail text,
  applied_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(publication_id_a,publication_id_b),
  FOREIGN KEY (publication_id_a,publication_id_b)
    REFERENCES news_event_link_reviews(publication_id_a,publication_id_b)
    ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_news_event_review_applications_event
  ON news_event_review_applications(event_id);
