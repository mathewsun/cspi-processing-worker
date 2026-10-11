# Candidate matching v1 (staging only)

This stage deliberately **does not** auto-merge events or assign countries to the final CSPI geographic relations.

- `news_country_candidates`: explicit English country-name mentions in the headline and first 4,000 characters of body; evidence = matched country name; confidence 0.5. This can miss translations and can produce ambiguous names (e.g. Georgia).
- `news_near_duplicate_candidates`: Jaccard similarity of normalized title tokens >= 0.8, at least five distinct tokens per title, candidates within +/-24 hours, at most 250 recent articles compared per job.
- Both tables use idempotent unique keys and are written in the same transaction as the processing job completion.
- Exact SHA-256 deduplication remains in `news_article_fingerprints`.
- No AI embeddings, multilingual NER, event clustering or final `EventCountries`/`PublicationCountries` assignments yet.

## Test-only setup

Apply `sql/003_analysis_candidates.sql` after `sql/001_processing_queue.sql` and `sql/002_staging_bridge.sql` in a staging database. New code requires the tables to exist. Do not deploy before migrations and build/integration tests.

## Limitations and next iteration

- RSS staging currently stores title and URL, not article body. Many matches will be headline-only.
- English country names are incomplete for non-English publications. Add multilingual gazetteer, entity disambiguation, location vs affected-country roles.
- Title token Jaccard can miss paraphrases and match unrelated recurring headlines. Treat scores as suggestions, not verified duplicates.
- A 250-item comparison window is an initial cap, not a scalable nearest-neighbor index; use embeddings + ANN/vector search at larger volumes.
- Country mentions are not country impact: distinguish Primary/Secondary/Mentioned using an explicit classifier with evidence and confidence.
- Use staging load tests to validate performance before enabling at 10-minute polling cadence.
