-- Apply after 001..018 and after installing pgvector in the PostgreSQL image.
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE IF NOT EXISTS news_local_semantic_control(
 id boolean PRIMARY KEY DEFAULT true CHECK(id),enabled boolean NOT NULL DEFAULT false,
 model_revision text NOT NULL,model_sha text NOT NULL,next_event_slot timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS news_local_semantic_runs(
 model_revision text PRIMARY KEY,snapshot_max_id int NOT NULL,archive_cursor bigint NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS news_local_semantic_jobs(
 publication_id int NOT NULL REFERENCES "Publications"("Id"),model_revision text NOT NULL,
 queue_kind text NOT NULL DEFAULT 'Live',state text NOT NULL DEFAULT 'Pending',dirty_generation bigint NOT NULL DEFAULT 0,observed_generation bigint NOT NULL DEFAULT -1,
 next_at timestamptz NOT NULL DEFAULT now(),lease_token text,lease_until timestamptz,attempts int NOT NULL DEFAULT 0,last_error text,processed_at timestamptz,
 PRIMARY KEY(publication_id,model_revision));
CREATE INDEX IF NOT EXISTS news_local_semantic_due ON news_local_semantic_jobs(model_revision,state,queue_kind,next_at,publication_id);
CREATE TABLE IF NOT EXISTS news_local_semantic_vectors(
 publication_id int NOT NULL REFERENCES "Publications"("Id"),model_revision text NOT NULL,input_hash text NOT NULL,
 embedding vector(384) NOT NULL,title text NOT NULL,body text,published_at timestamptz NOT NULL,source_id int,
 embedded_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(publication_id,model_revision));
CREATE INDEX IF NOT EXISTS news_local_semantic_ann ON news_local_semantic_vectors USING hnsw(embedding vector_cosine_ops) WITH(m=12,ef_construction=64);
CREATE INDEX IF NOT EXISTS news_local_semantic_time ON news_local_semantic_vectors(model_revision,published_at);
CREATE TABLE IF NOT EXISTS news_local_semantic_pairs(
 a int NOT NULL REFERENCES "Publications"("Id"),b int NOT NULL REFERENCES "Publications"("Id"),model_revision text NOT NULL,
 hash_a text NOT NULL,hash_b text NOT NULL,similarity real NOT NULL CHECK(similarity BETWEEN -1 AND 1),
 state text NOT NULL DEFAULT 'Pending' CHECK(state IN('Pending','RelatedOnly','Completed','Failed','Invalidated')),
 decision text,confidence double precision,reason text,judge_revision text,
 lease_token text,lease_until timestamptz,attempts int NOT NULL DEFAULT 0,next_at timestamptz NOT NULL DEFAULT now(),
 created_at timestamptz NOT NULL DEFAULT now(),judged_at timestamptz,
 PRIMARY KEY(a,b,model_revision),CHECK(a<b));
CREATE INDEX IF NOT EXISTS news_local_semantic_pairs_b ON news_local_semantic_pairs(b,model_revision);
CREATE INDEX IF NOT EXISTS news_local_semantic_judge_due ON news_local_semantic_pairs(state,next_at,similarity DESC);
CREATE INDEX IF NOT EXISTS news_local_semantic_pairs_recent ON news_local_semantic_pairs(created_at DESC);
CREATE TABLE IF NOT EXISTS news_local_semantic_judge_audit(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,a int NOT NULL,b int NOT NULL,model_revision text NOT NULL,
 hash_a text NOT NULL,hash_b text NOT NULL,judge_revision text NOT NULL,decision text NOT NULL,confidence double precision NOT NULL,reason text NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
CREATE OR REPLACE FUNCTION news_local_semantic_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='UPDATE' AND OLD."Title" IS NOT DISTINCT FROM NEW."Title" AND OLD."OriginalText" IS NOT DISTINCT FROM NEW."OriginalText" AND OLD."PublishedAt"=NEW."PublishedAt" AND OLD."SourceId" IS NOT DISTINCT FROM NEW."SourceId" THEN RETURN NULL; END IF;
 IF btrim(NEW."Title")='' OR EXISTS(SELECT 1 FROM "Sources" WHERE "Id"=NEW."SourceId" AND "CollectionKind"='Social') THEN
  UPDATE news_local_semantic_jobs SET state='Skipped',dirty_generation=dirty_generation+1 WHERE publication_id=NEW."Id";
  RETURN NULL;
 END IF;
 INSERT INTO news_local_semantic_jobs(publication_id,model_revision)
 SELECT NEW."Id",model_revision FROM news_local_semantic_control WHERE enabled
 ON CONFLICT(publication_id,model_revision) DO UPDATE SET dirty_generation=news_local_semantic_jobs.dirty_generation+1,state='Pending',queue_kind='Live',next_at=now(),attempts=0,last_error=NULL;
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS news_local_semantic_dirty ON "Publications";
CREATE TRIGGER news_local_semantic_dirty AFTER INSERT OR UPDATE OF "Title","OriginalText","PublishedAt","SourceId" ON "Publications" FOR EACH ROW EXECUTE FUNCTION news_local_semantic_dirty();
