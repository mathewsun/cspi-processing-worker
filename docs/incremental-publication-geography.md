# Incremental publication geography (staging only)

This PR adds a durable dirty-publication queue and a separate opt-in incremental reconciler.

- SQL 014 creates news_publication_geography_dirty and a trigger on INSERT/UPDATE/DELETE of news_country_role_proposals.
- Initial backfill queues publications that already have proposals.
- IncrementalPublicationGeographyService processes batches (PublicationGeography__BatchSize=100, max 500), using FOR UPDATE SKIP LOCKED and the existing geography advisory lock.
- Accepted proposals define the desired country-role relations; any Rejected proposal for the same publication/country vetoes automatic classification.
- Only known worker Method values are deleted or updated. Other rows are left alone.
- Country changes trigger the existing SQL 013 event geography dirty queue.
- PublicationGeography__Enabled=false by default.

**Important:** Keep CountryRelations__Enabled=false when enabling PublicationGeography__Enabled; the legacy full-scan reconciler is still registered and must not run concurrently. Neither should be enabled in production yet.

Remaining work: provenance/ownership field instead of Method heuristics; test trigger updates and transactional consistency; replace global advisory lock with per-publication locking; benchmarks; build and PostgreSQL integration tests. A manual correction that retains an automation Method remains at risk of being overwritten. The queue does not guarantee a ten-minute collection interval; that is a separate News Worker requirement.
