# Accepted geographic relationships (staging)

The new `CountryRelationsMaterializer` bridges the proposal queue to the CSPI relational model without treating unverified country mentions as confirmed facts.

- `CountryRelations__Enabled=false` by default.
- Every 30 seconds, insert **Accepted** `news_country_role_proposals` into `PublicationCountries`. Pending and Rejected proposals are excluded.
- `Primary=0`, `Secondary=1`, `Mentioned=2`, matching CSPI's `CountryRelationRole` enum.
- Existing relationships are not overwritten; manual corrections retain priority.
- Only **single-publication** events receive automatically inherited country relationships. Multi-article events need evidence aggregation and a conflict-aware consensus algorithm.
- Uses a transaction and advisory lock for concurrent replicas. Repeated runs are idempotent via ON CONFLICT DO NOTHING.
- A proposal is Accepted only by a separate reviewer or calibrated model workflow. This PR does not accept proposals automatically.
- Does not alter the legacy `Country` text field or infer roles from the source's location.

## Limitations

This is not full AI country-role classification. Existing relations are insert-only; if a proposal is later rejected, there is no automatic rollback. Adding/removing event members can leave stale inherited relations; a reconciliation and provenance mechanism is required before production. The processing service remains disabled by default. Build and PostgreSQL integration tests are pending.

Requires CSPI's multi-country migration and Processing Worker SQL 009 in a staging database.
