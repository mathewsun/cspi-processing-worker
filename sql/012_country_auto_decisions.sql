-- Reviewable automation decisions; no destructive changes to existing proposals.
CREATE TABLE IF NOT EXISTS news_country_auto_decisions (
 publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
 country_profile_id integer NOT NULL REFERENCES "Countries"("Id") ON DELETE CASCADE,
 method varchar(48) NOT NULL,
 decision varchar(24) NOT NULL CHECK(decision IN ('Eligible','Accepted','Skipped')),
 reason text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(publication_id,country_profile_id,method)
);
CREATE INDEX IF NOT EXISTS ix_news_country_auto_decisions_status ON news_country_auto_decisions(decision,created_at);
