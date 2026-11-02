-- Local model queue extension. Apply after 017; no historical classifications are overwritten.
BEGIN;
ALTER TABLE news_country_llm_jobs
 ADD COLUMN IF NOT EXISTS queue_kind text NOT NULL DEFAULT 'Archive',
 ADD COLUMN IF NOT EXISTS started_at timestamptz,
 ADD COLUMN IF NOT EXISTS input_hash text,
 ADD COLUMN IF NOT EXISTS input_scope text,
 ADD COLUMN IF NOT EXISTS result_json jsonb;
CREATE INDEX IF NOT EXISTS ix_country_local_due
 ON news_country_llm_jobs(model_id,queue_kind,publication_id)
 WHERE state<>'Completed' AND attempts<3;
CREATE TABLE IF NOT EXISTS news_country_ai_runs (
 model_id varchar(120) PRIMARY KEY,
 model_name text NOT NULL, model_digest text NOT NULL, prompt_version text NOT NULL,
 snapshot_max_id integer NOT NULL,
 archive_cursor integer NOT NULL DEFAULT 0,
 live_cursor integer NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS news_country_ai_results (
 publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
 model_id varchar(120) NOT NULL REFERENCES news_country_ai_runs(model_id),
 input_hash text NOT NULL, input_scope text NOT NULL, result_json jsonb NOT NULL,
 classified_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(publication_id,model_id,input_hash)
);
CREATE OR REPLACE FUNCTION news_recheck_country_changed_article()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."Title" IS DISTINCT FROM NEW."Title" OR OLD."OriginalText" IS DISTINCT FROM NEW."OriginalText" THEN
  UPDATE news_country_llm_jobs j SET state='Pending',attempts=0,next_attempt_at=now(),
    lease_owner=NULL,lease_until=NULL,processed_at=NULL,last_error=NULL,started_at=NULL,input_hash=NULL,input_scope=NULL,result_json=NULL
  WHERE j.publication_id=NEW."Id" AND EXISTS(SELECT 1 FROM news_country_ai_runs r WHERE r.model_id=j.model_id);
 END IF;
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_country_ai_article_changed ON "Publications";
CREATE TRIGGER trg_country_ai_article_changed AFTER UPDATE OF "Title","OriginalText" ON "Publications"
 FOR EACH ROW EXECUTE FUNCTION news_recheck_country_changed_article();

CREATE OR REPLACE FUNCTION news_exclude_publication_country_manual(p_id integer,c_id integer,actor text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 IF length(trim(coalesce(actor,'')))=0 THEN RAISE EXCEPTION 'Actor required'; END IF;
 PERFORM pg_advisory_xact_lock(23011,8);
 IF NOT EXISTS(SELECT 1 FROM "Publications" WHERE "Id"=p_id) THEN RAISE EXCEPTION 'Publication not found'; END IF;
 DELETE FROM news_publication_country_ownership WHERE publication_id=p_id AND country_profile_id=c_id;
 DELETE FROM "PublicationCountries" WHERE "PublicationId"=p_id AND "CountryProfileId"=c_id;
 INSERT INTO news_geography_manual_overrides(target_type,target_id,country_profile_id,is_excluded,changed_by)
 VALUES('Publication',p_id,c_id,true,actor)
 ON CONFLICT(target_type,target_id,country_profile_id) DO UPDATE
 SET is_excluded=true,changed_by=excluded.changed_by,changed_at=now();
 INSERT INTO news_publication_geography_dirty(publication_id) VALUES(p_id)
 ON CONFLICT(publication_id) DO UPDATE SET changed_at=clock_timestamp();
END $$;
COMMIT;
