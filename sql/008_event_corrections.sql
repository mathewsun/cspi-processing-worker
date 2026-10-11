-- Apply to staging before enabling autonomous event links.
-- Human decisions and corrections take precedence over model recommendations.
CREATE TABLE IF NOT EXISTS news_event_pair_blocks (
    publication_id_a integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    publication_id_b integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(publication_id_a,publication_id_b),
    CHECK(publication_id_a < publication_id_b)
);
CREATE TABLE IF NOT EXISTS news_event_actions (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id integer REFERENCES "Events"("Id") ON DELETE SET NULL,
    publication_id integer REFERENCES "Publications"("Id") ON DELETE SET NULL,
    action text NOT NULL,
    actor text NOT NULL,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_news_event_actions_event ON news_event_actions(event_id,created_at DESC);
