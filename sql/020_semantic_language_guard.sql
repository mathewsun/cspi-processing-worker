-- Apply before enabling LocalSemantic:AutoGroup on an existing installation.
ALTER TABLE news_local_semantic_vectors ADD COLUMN IF NOT EXISTS language varchar(12);
ALTER TABLE news_local_semantic_vectors ADD COLUMN IF NOT EXISTS language_confidence real;
-- Existing vectors stay available for audit, but cannot authorize automatic links
-- until language detection has been performed on the current input.
UPDATE news_local_semantic_jobs j SET state='Pending',dirty_generation=dirty_generation+1,
 next_at=now(),lease_token=NULL,lease_until=NULL,attempts=0
FROM news_local_semantic_vectors v WHERE v.publication_id=j.publication_id AND v.model_revision=j.model_revision
 AND v.language IS NULL AND j.state='Completed';
