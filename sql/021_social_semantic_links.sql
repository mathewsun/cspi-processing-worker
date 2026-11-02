-- Requires the application's social_article_links migration and semantic SQL 019/020.
BEGIN;
CREATE TABLE IF NOT EXISTS social_semantic_jobs(
 post_id bigint PRIMARY KEY REFERENCES social_posts(id),next_at timestamptz NOT NULL DEFAULT now(),
 queue_kind text NOT NULL DEFAULT 'Live' CHECK(queue_kind IN('Live','Archive')),
 lease_token text,lease_until timestamptz,attempts int NOT NULL DEFAULT 0,
 processed_at timestamptz,last_result text);
CREATE TABLE IF NOT EXISTS social_semantic_decisions(
 post_id bigint NOT NULL REFERENCES social_posts(id),publication_id int NOT NULL REFERENCES "Publications"("Id"),
 post_hash text NOT NULL,article_hash text NOT NULL,model_revision text NOT NULL,
 decision text NOT NULL,confidence double precision NOT NULL,reason text NOT NULL,
 similarity double precision NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(post_id,publication_id,post_hash,article_hash,model_revision));
CREATE OR REPLACE FUNCTION social_semantic_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_NAME='social_posts' THEN
  IF TG_OP='UPDATE' AND OLD.body_hash=NEW.body_hash AND OLD.deleted=NEW.deleted AND OLD.published_at IS NOT DISTINCT FROM NEW.published_at THEN RETURN NULL; END IF;
  INSERT INTO social_semantic_jobs(post_id) VALUES(NEW.id) ON CONFLICT(post_id) DO UPDATE SET queue_kind='Live',next_at=now(),lease_token=NULL,lease_until=NULL,attempts=0;
  WITH removed AS(UPDATE social_article_links SET state='AutoRemoved',changed_at=now()
   WHERE post_id=NEW.id AND state='AutoConfirmed' AND changed_by='semantic-social-v1' RETURNING publication_id,post_id,evidence)
  INSERT INTO social_article_link_audit(publication_id,post_id,previous_state,new_state,evidence,actor)
   SELECT publication_id,post_id,'AutoConfirmed','AutoRemoved',evidence,'semantic-social-v1' FROM removed;
 ELSE
  IF OLD."Title" IS NOT DISTINCT FROM NEW."Title" AND OLD."OriginalText" IS NOT DISTINCT FROM NEW."OriginalText" AND OLD."PublishedAt" IS NOT DISTINCT FROM NEW."PublishedAt" AND OLD."SourceId" IS NOT DISTINCT FROM NEW."SourceId" THEN RETURN NULL; END IF;
  UPDATE social_semantic_jobs SET next_at=now(),lease_token=NULL,lease_until=NULL,attempts=0 WHERE post_id IN(SELECT post_id FROM social_article_links WHERE publication_id=NEW."Id" AND changed_by='semantic-social-v1');
  WITH removed AS(UPDATE social_article_links SET state='AutoRemoved',changed_at=now()
   WHERE publication_id=NEW."Id" AND state='AutoConfirmed' AND changed_by='semantic-social-v1' RETURNING publication_id,post_id,evidence)
  INSERT INTO social_article_link_audit(publication_id,post_id,previous_state,new_state,evidence,actor)
   SELECT publication_id,post_id,'AutoConfirmed','AutoRemoved',evidence,'semantic-social-v1' FROM removed;
 END IF;
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS social_semantic_post_dirty ON social_posts;
CREATE TRIGGER social_semantic_post_dirty AFTER INSERT OR UPDATE OF body_hash,deleted,published_at ON social_posts FOR EACH ROW EXECUTE FUNCTION social_semantic_dirty();
DROP TRIGGER IF EXISTS social_semantic_news_dirty ON "Publications";
CREATE TRIGGER social_semantic_news_dirty AFTER UPDATE OF "Title","OriginalText","PublishedAt","SourceId" ON "Publications" FOR EACH ROW EXECUTE FUNCTION social_semantic_dirty();
INSERT INTO social_semantic_jobs(post_id,queue_kind) SELECT id,'Archive' FROM social_posts ON CONFLICT DO NOTHING;
CREATE INDEX IF NOT EXISTS social_semantic_jobs_due ON social_semantic_jobs(next_at,post_id);
COMMIT;
