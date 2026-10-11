-- Conservative geographic role proposals. No automatic final assignment.
CREATE TABLE IF NOT EXISTS news_country_role_proposals (
  publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
  country_profile_id integer NOT NULL REFERENCES "Countries"("Id") ON DELETE CASCADE,
  proposed_role varchar(16) NOT NULL CHECK(proposed_role IN ('Primary','Secondary','Mentioned')),
  confidence real NOT NULL CHECK(confidence BETWEEN 0 AND 1),
  method varchar(48) NOT NULL,
  evidence text NOT NULL,
  status varchar(16) NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Accepted','Rejected')),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(publication_id,country_profile_id,method)
);
CREATE INDEX IF NOT EXISTS ix_news_country_role_proposals_pending
 ON news_country_role_proposals(created_at) WHERE status='Pending';
