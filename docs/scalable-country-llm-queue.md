# Scalable country classification queue — staging only

This iteration removes the single-replica job-claim limitation from the experimental LLM country classifier.

- Apply `sql/011_country_llm_leases.sql` to staging after `sql/010_llm_country_jobs.sql`.
- Claims use PostgreSQL `FOR UPDATE SKIP LOCKED`, a two-minute lease, unique instance owner, and owner-checked completion/failure updates.
- `CountryLlm__AutoEnqueue=false` (default). When explicitly enabled, the scheduler adds processed publications with at least 100 characters of original text to the LLM queue.
- `CountryLlm__EnqueueBatch=100` and `CountryLlm__MaxPending=1000` bound each cycle and outstanding backlog.
- `CountryLlm__Enabled=false` remains the default for inference. Neither service accepts geographic proposals automatically.
- `CountryLlm__Model` selects the model-specific queue.

## Safety and scaling limitations

Leases expire after two minutes. Slow inference or stalled DB transactions can cause duplicate model requests; completion is owner-checked and proposal inserts are idempotent. Before production, add heartbeat renewal and bounded inference request/response size, per-provider rate limits, budget caps, persistent model-version provenance, dead-letter controls and queue telemetry.

Auto-enqueue selects newest eligible publications first, so old backlogs may starve under sustained load; a cursor/fair scheduling policy is needed. A strict 10-minute news collection interval does not imply that AI analysis finishes in 10 minutes.

No model is configured, no inference is enabled, no classification is auto-accepted, and no live integration tests have been run. Do not deploy to production without a tested database migration and load testing.
