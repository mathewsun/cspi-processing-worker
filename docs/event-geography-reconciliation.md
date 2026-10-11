# Event geography reconciliation (staging only)

This PR adds a disabled-by-default `EventGeographyReconciliationService` and makes it the sole worker writer of EventCountries. `CountryRelationsMaterializer` still inserts accepted publication-country relationships but no longer inserts EventCountries directly.

## Behavior

- `EventGeography__Enabled=false` by default; runs every two minutes when explicitly enabled.
- Single-article event: inherit accepted PublicationCountries.
- Multi-article event: a country is assigned only if **every** linked article has that country with the **same** role and at least two distinct non-null SourceId values support it. Otherwise abstain.
- On merge/split or country-role changes, remove obsolete *worker-owned* EventCountries rows (`SingletonArticleV1`, `EventConsensusV1`) and insert current consensus.
- Manually assigned EventCountries rows with other Method values are never deleted or overwritten.
- PostgreSQL advisory lock serializes changes with event linking/splitting.

## Limitations before production

- This is a strict, conservative consensus, not factual AI country classification. It can omit true cross-border countries if a source omits one. Repeated articles from different SourceId values are not necessarily independent.
- Existing manual edits that reuse an automation Method value cannot be distinguished; manual editing must stamp a distinct Method such as `Manual`.
- `PublicationCountries` are currently insert-only in the upstream materializer: rejected or revised proposals can leave stale publication geography. Fix that and add provenance before enabling.
- This prototype scans all event memberships every two minutes. For 10k–100k sources, use a durable dirty-event queue, incremental reconciliation and indexes.
- Does not update the legacy Events.Country string or produce a human-facing audit of every change.
- No build or PostgreSQL integration tests have been run. Do not enable against production yet.

## Test scenarios

1. Singleton accepted country -> inherited event-country link.
2. Second publication joins with same role -> consensus role remains.
3. Second publication disagrees or lacks country -> worker-owned link removed.
4. Manual split -> recalculate both resulting events without restoring blocked article links.
5. Manual EventCountries assignment survives all reconciliations.
6. Two concurrent replicas produce no duplicate country links.
