-- Candidate event links requiring verification before actual Events are created.
-- Apply to a staging DB after scripts 001..005.
CREATE TABLE IF NOT EXISTS news_event_link_reviews (
    publication_id_a integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    publication_id_b integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    score real NOT NULL CHECK (score >= 0 AND score <= 1),
    evidence jsonb NOT NULL,
    decision varchar(16) NOT NULL DEFAULT 'Pending'
        CHECK (decision IN ('Pending','Approved','Rejected')),
    reviewed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(publication_id_a,publication_id_b),
    CHECK(publication_id_a < publication_id_b)
);
CREATE INDEX IF NOT EXISTS ix_news_event_link_reviews_pending
    ON news_event_link_reviews(created_at) WHERE decision='Pending';

-- Read-only operational report; no automatic approval.
SELECT decision, count(*) FROM news_event_link_reviews GROUP BY decision;
