# Event-link review candidates — v1

This is an **opt-in staging step**, not automatic event creation.

## Purpose

News from different publishers may describe the same event. Candidate pairs are produced from existing semantic cosine similarity and title-token similarity. This step does not infer that a match is correct, merge publications, or change country relations.

## Configuration

- `EventReview__Enabled=false` (default)
- `EventReview__SemanticThreshold=0.93`
- `EventReview__TitleThreshold=0.90`
- `EventReview__BatchSize=500`

Apply `sql/006_event_link_reviews.sql` in a **test** database after scripts 001–005. It adds `news_event_link_reviews`, with `Pending`, `Approved`, and `Rejected` decisions.

A pair qualifies only if:
- It is from two distinct `SourceId` values (these can still share ownership/syndication).
- Publication timestamps are within 12 hours.
- At least one configured similarity threshold is reached.
- The pair is not already present, including previously rejected pairs.

Evidence records the model score (if any), title score (if any), source IDs and timestamps. An approval must be an explicit later action with additional fact checks. No automatic `Events` or `EventPublications` writes are performed.

## Known limitations

- Timestamp proximity and similarity do not prove identical facts. Follow-up updates and recurring events can be mistaken for duplicates.
- Multiple article pairs can form inconsistent clusters; future event creation needs graph constraints, named-entity/date/place checks and cluster review.
- Candidate generation scans existing candidate tables periodically and is not a scalable incremental event queue. Before 100k sources, add durable cursor/leases, indexes, and load tests.
- The embedding service is still disabled by default and requires a separately hosted model.
- A country *mention* is not a Primary/Secondary/Mentioned classification. Geographic relationships remain unassigned.
- The service is disabled by default and has not been compiled or integration-tested in this environment.

## Next steps

1. Validate candidate precision on a labeled multilingual dataset.
2. Implement reviewed event clustering and event-publication association.
3. Add role classification with evidence for countries.
4. Benchmark queue throughput and end-to-end latency.
