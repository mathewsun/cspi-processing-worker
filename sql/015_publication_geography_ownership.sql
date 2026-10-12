-- Staging migration. Explicit ownership for automated publication geography.
-- Existing records are NOT automatically marked worker-owned.
CREATE TABLE IF NOT EXISTS news_publication_country_ownership (
    publication_id integer NOT NULL,
    country_profile_id integer NOT NULL,
    owner varchar(48) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (publication_id,country_profile_id),
    FOREIGN KEY (publication_id,country_profile_id)
      REFERENCES "PublicationCountries"("PublicationId","CountryProfileId")
      ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_news_publication_country_ownership_owner
    ON news_publication_country_ownership(owner);

-- No automatic historical backfill: Method is not reliable proof of ownership.
-- Historical records must be reviewed before they can be managed by the worker.
