-- Candidate analysis tables. These do NOT create events or assert verified country roles.
-- Run on a test CSPI database after 001_processing_queue.sql.
CREATE TABLE IF NOT EXISTS news_country_candidates (
    publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    country_profile_id integer NOT NULL REFERENCES "Countries"("Id") ON DELETE CASCADE,
    method varchar(32) NOT NULL DEFAULT 'ExplicitNameV1',
    evidence text NOT NULL,
    confidence real NOT NULL DEFAULT 0.5,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (publication_id, country_profile_id, method)
);
CREATE INDEX IF NOT EXISTS ix_news_country_candidates_country
    ON news_country_candidates(country_profile_id);

CREATE TABLE IF NOT EXISTS news_near_duplicate_candidates (
    publication_id_a integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    publication_id_b integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
    similarity real NOT NULL,
    method varchar(32) NOT NULL DEFAULT 'TitleTokensV1',
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(publication_id_a, publication_id_b),
    CHECK(publication_id_a < publication_id_b)
);
CREATE INDEX IF NOT EXISTS ix_news_near_duplicate_candidates_b
    ON news_near_duplicate_candidates(publication_id_b);
