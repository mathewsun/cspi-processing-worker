# CSPI Processing Worker

Independent .NET 10 service for processing collected news.

**This is a first-stage prototype, not production-ready. Do not deploy to the production database yet.**

## Pipeline

1. CSPI News Worker collects source articles (currently a separate staging table).
2. An adapter (not yet implemented) promotes staged articles to CSPI `Publications` and queues `news_processing_jobs`.
3. Processing Worker leases publications using PostgreSQL `FOR UPDATE SKIP LOCKED`.
4. The initial implementation normalizes URLs/text and identifies *exact* duplicates with SHA-256.
5. Future processing stages will detect syndication/near-duplicates, compute multilingual embeddings, cluster shared events and classify countries.

## Files

- `ProcessingService.cs` – standalone, horizontally shareable job consumer.
- `sql/001_processing_queue.sql` – development tables for queue and exact fingerprints.
- `Dockerfile` – separate .NET 10 container.

## Future semantic processing

- SimHash/MinHash for near-identical syndication copies.
- Multilingual sentence/document embeddings, ANN index, temporal and entity constraints for event clustering.
- Country classification with `Primary`, `Secondary`, `Mentioned` and confidence plus model version.
- Write country associations to CSPI's `PublicationCountries` and `EventCountries`; event clusters to `EventPublications`.
- Preserve independent sources and article attribution, even when clustered into one event.

## Required before production

- Integrate with `news_collected_items` and `Publications`.
- Install reviewed migrations on staging and add queue synchronization.
- Add idempotent processing, retry limits, dead-letter handling and lease renewal.
- Handle publication source and country correctly; avoid merging unrelated articles by title.
- Address data privacy, publisher rights, article attribution and safety.
- Add metrics, integration tests, benchmarks and scalable embeddings infrastructure.

The initial code does not perform semantic matching or country inference. These are separate future stages.
