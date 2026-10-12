-- Staging-only migration. Do not claim historical event-country rows automatically.
CREATE TABLE IF NOT EXISTS news_event_country_ownership (
    event_id integer NOT NULL,
    country_profile_id integer NOT NULL,
    owner varchar(48) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (event_id,country_profile_id),
    FOREIGN KEY (event_id,country_profile_id)
      REFERENCES "EventCountries"("EventId","CountryProfileId")
      ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_news_event_country_ownership_owner
  ON news_event_country_ownership(owner);
-- Historical Method strings are not proof of worker ownership.
