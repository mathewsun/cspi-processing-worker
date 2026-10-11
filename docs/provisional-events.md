# Automatic provisional events — staging prototype

This stage removes the need for a human to approve every news item before it becomes an event.

- `SingletonEvents__Enabled=false` by default.
- `SingletonEvents__BatchSize=100` (1–500).
- Only articles with completed `news_processing_jobs`, older than 30 minutes, without an EventPublications link and without pending/approved event-link candidates qualify.
- Creates one provisional `Events` row and one `EventPublications` link per eligible article in a transaction.
- Uses the same PostgreSQL advisory lock as the existing approved-event materializer and manual split endpoint, so concurrent workers cannot duplicate event graph changes.
- Sets legacy Event.Country to `Unclassified` — no inferred country roles are claimed.
- This stage does not delete source publications or require reviewer action.

## Critical limitation

This does **not** yet solve merging two provisional singleton events when a late-arriving article match is discovered. The existing ApprovedEventService records a Conflict when articles belong to different events. That means this implementation is **not ready for enabling alongside automatic event linking** until safe event merge with manual split-block enforcement and reversible audit is implemented. The 30-minute delay reduces, but does not eliminate, that possibility.

Also required before production: end-to-end integration tests, idempotency/race tests, country role inference, labeled-data precision calibration, robust job leases, and collector security hardening.

Do not enable in production. No new schema is introduced by this PR; it depends on CSPI's EventPublications and the processing job/review tables.
