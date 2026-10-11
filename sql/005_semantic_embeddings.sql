-- Staging-only semantic candidate store; no pgvector dependency in this iteration.
-- Do not mix embeddings from different model IDs or dimensions.
CREATE TABLE IF NOT EXISTS news_semantic_embeddings (
  publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
  model_id text NOT NULL,
  dimensions integer NOT NULL CHECK (dimensions BETWEEN 8 AND 4096),
  embedding real[] NOT NULL,
  embedded_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (publication_id, model_id)
);
CREATE INDEX IF NOT EXISTS ix_news_semantic_embeddings_model
  ON news_semantic_embeddings (model_id, embedded_at DESC);

CREATE TABLE IF NOT EXISTS news_semantic_candidates (
  publication_id_a integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
  publication_id_b integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
  model_id text NOT NULL,
  cosine_similarity real NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (publication_id_a, publication_id_b, model_id),
  CHECK (publication_id_a < publication_id_b)
);
CREATE INDEX IF NOT EXISTS ix_news_semantic_candidates_b
  ON news_semantic_candidates(publication_id_b);

CREATE TABLE IF NOT EXISTS news_semantic_failures (
  publication_id integer NOT NULL REFERENCES "Publications"("Id") ON DELETE CASCADE,
  model_id text NOT NULL,
  attempts integer NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  last_error text,
  PRIMARY KEY (publication_id, model_id)
);
