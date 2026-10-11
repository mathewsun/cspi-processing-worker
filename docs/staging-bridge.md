# Staging-to-Publications bridge (development)

This branch connects the two existing stages without running semantic AI inference yet:

```
cspi-news-worker
  → news_collected_items (one row per source + URL)
  → StagingPromotionService
  → CSPI Publications + news_staging_publications mapping
  → news_processing_jobs
  → ProcessingService (exact SHA-256 fingerprints)
```

The promoter uses a transaction and `FOR UPDATE SKIP LOCKED` on staging rows, so concurrent replicas cannot promote the same staging row simultaneously. The mapping is keyed by staging item ID and the processing queue by publication ID.

## Staging setup only

1. Apply the collector's `sql/001_collector_jobs.sql` to a test CSPI database.
2. Apply this repository's `sql/001_processing_queue.sql`.
3. Apply `sql/002_staging_bridge.sql`.
4. Start the worker only with a test database connection.

## Known limitations before production

- Promotion currently uses `Sources.Country` as a **legacy placeholder**, not geographic inference. This may be incorrect for international news. Do not treat it as the final classification.
- Promotion creates one CSPI publication for each distinct collector staging item. Cross-source exact duplicates are only **marked later**, not deleted or clustered.
- The news collector itself still needs safe network controls, feed discovery, scheduling, retries, and integration testing.
- The processing worker does not yet perform SimHash, multilingual embeddings, event clustering, or country classification.
- Schema setup uses manual SQL, not managed EF migrations. Do not apply it directly to production.
- Run real PostgreSQL integration tests, load tests, and check the exact deployed schema before activation.
- Future changes should introduce an outbox or trigger for new staging records if asynchronous notification becomes necessary. Current promoter polls every 3 seconds when idle.

No production deployment is enabled by this PR.
