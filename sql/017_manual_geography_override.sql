-- Staging only. Persistent manual vetoes and atomic correction functions.
CREATE TABLE IF NOT EXISTS news_geography_manual_overrides (
  target_type varchar(16) NOT NULL CHECK(target_type IN ('Publication','Event')),
  target_id integer NOT NULL,
  country_profile_id integer NOT NULL REFERENCES "Countries"("Id"),
  is_excluded boolean NOT NULL DEFAULT false,
  changed_by text NOT NULL,
  changed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(target_type,target_id,country_profile_id)
);

-- Manual correction is a single transaction: revoke worker ownership and
-- upsert a manually owned link. Use from an authorized application endpoint.
CREATE OR REPLACE FUNCTION news_set_publication_country_manual(
  p_id integer, c_id integer, role_id integer, actor text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF role_id NOT BETWEEN 0 AND 2 OR length(trim(actor))=0 THEN
    RAISE EXCEPTION 'Invalid manual country correction';
  END IF;
  PERFORM pg_advisory_xact_lock(23011,8);
  DELETE FROM news_publication_country_ownership
   WHERE publication_id=p_id AND country_profile_id=c_id;
  INSERT INTO "PublicationCountries"
    ("PublicationId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
  VALUES (p_id,c_id,role_id,1,'Manual',now())
  ON CONFLICT ("PublicationId","CountryProfileId") DO UPDATE
  SET "Role"=excluded."Role","Confidence"=1,
      "Method"='Manual',"ClassifiedAt"=now();
  INSERT INTO news_geography_manual_overrides
    (target_type,target_id,country_profile_id,is_excluded,changed_by,changed_at)
  VALUES ('Publication',p_id,c_id,false,actor,now())
  ON CONFLICT(target_type,target_id,country_profile_id) DO UPDATE
  SET is_excluded=false,changed_by=excluded.changed_by,changed_at=now();
END $$;

CREATE OR REPLACE FUNCTION news_set_event_country_manual(
  e_id integer, c_id integer, role_id integer, actor text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF role_id NOT BETWEEN 0 AND 2 OR length(trim(actor))=0 THEN
    RAISE EXCEPTION 'Invalid manual country correction';
  END IF;
  PERFORM pg_advisory_xact_lock(23011,7);
  DELETE FROM news_event_country_ownership
   WHERE event_id=e_id AND country_profile_id=c_id;
  INSERT INTO "EventCountries"
    ("EventId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
  VALUES (e_id,c_id,role_id,1,'Manual',now())
  ON CONFLICT ("EventId","CountryProfileId") DO UPDATE
  SET "Role"=excluded."Role","Confidence"=1,
      "Method"='Manual',"ClassifiedAt"=now();
  INSERT INTO news_geography_manual_overrides
    (target_type,target_id,country_profile_id,is_excluded,changed_by,changed_at)
  VALUES ('Event',e_id,c_id,false,actor,now())
  ON CONFLICT(target_type,target_id,country_profile_id) DO UPDATE
  SET is_excluded=false,changed_by=excluded.changed_by,changed_at=now();
END $$;
