# Explicit publication geography ownership — staging only

Adds `sql/015_publication_geography_ownership.sql` and hardens `IncrementalPublicationGeographyService`.

## Rules

- A new `news_publication_country_ownership` table records which exact publication/country link was inserted by the incremental worker.
- Worker-owned rows are the only rows the incremental reconciler can update or delete.
- Existing rows, including historical links with `Method=LlmRoleV1`, are NOT automatically marked worker-owned.
- A new row and its ownership marker are inserted in one transaction. On deletion, the ownership marker cascades.
- If another actor inserted the row first, `ON CONFLICT DO NOTHING` does not claim ownership.
- Startup rejects simultaneously enabled legacy CountryRelations and incremental PublicationGeography writers.

## Staging validation

Apply SQL 015 before enabling the incremental worker. Test manual override, insert conflicts, accepted-to-rejected retractions, concurrent transactions, and historical unowned rows. In particular, manual correction to an already worker-owned row needs an explicit ownership-release workflow; that is not implemented here.

This PR does not harden EventCountries ownership, replace global advisory locks, run actual load tests, or validate the full pipeline. No build or integration tests were executed. Keep production flags disabled.
