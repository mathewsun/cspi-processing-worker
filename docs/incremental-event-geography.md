# Incremental event geography (experimental)

This PR replaces the full scan in EventGeographyReconciliationService with a durable dirty-event queue.

- `sql/013_incremental_event_geography.sql` creates `news_event_geography_dirty` and triggers on `EventPublications` and `PublicationCountries`.
- Event membership or publication geography changes enqueue the affected event ID, coalesced by primary key.
- Worker claims up to `EventGeography:BatchSize` (default 100) event IDs with `FOR UPDATE SKIP LOCKED`.
- Reconciliation retains the existing conservative country consensus and only deletes stale automation-owned links **within that batch**.
- Acknowledgment checks the claimed `changed_at` version to avoid discarding later updates.
- The same graph advisory lock protects against event merge/split operations.
- Existing events are initially seeded into the dirty queue for one-time reconciliation.

## Rollout
1. Apply SQL 013 to a **test database** after prior CSPI/processing migrations.
2. Verify trigger behavior for insert, delete and update of event links and publication-country links.
3. Test a 100-event batch, then multiple replicas and rollback/retry scenarios.
4. Keep `EventGeography__Enabled=false` on production until tests and database review pass.

## Known limitations
- The publication-country trigger on UPDATE currently queues the NEW publication; moving a link to another publication is not a supported normal operation and would need both old/new invalidation.
- Global graph advisory lock still serializes batches across replicas, limiting throughput. Move to per-event locks before large-scale deployment.
- Trigger overhead, event backfill and indexes must be benchmarked on realistic data.
- `CountryRelationsMaterializer` still scans accepted proposals globally. A separate incremental publication queue is needed.
- A manual classification that retains an automation Method may still be mistaken for worker-owned.
- Build and PostgreSQL integration tests have not been executed.
