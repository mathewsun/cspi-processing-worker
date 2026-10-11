# Publication-country retractions (staging only)

This change makes the existing CountryRelationsMaterializer reconcile worker-owned PublicationCountries links rather than only insert them.

- Accepted country-role proposals determine desired country links.
- Any Rejected proposal for the same publication/country vetoes automatic classification (conservative precedence).
- Stale links with known worker Method values (LlmRoleV1 or ExplicitMentionV1) are removed.
- Existing worker-owned role/confidence can be updated; missing links inserted.
- Rows with other Method values, including Manual, are never overwritten or removed.
- The downstream EventGeographyReconciliationService can then remove obsolete event geography on its next run.
- The service remains disabled by default; no new SQL migration is required.

**Limitations:** Existing worker rows are identified by their Method strings because the schema has no explicit owner/provenance column. Any manually edited row retaining one of those Method strings could be overwritten. This is not safe for production until ownership/provenance is made explicit and historical data audited. Full-table reconciliation remains unsuitable for very large volumes; add incremental dirty-publication/event queues.

Before activation, test Accepted→Rejected, role revision, manual override, multi-article event changes, and concurrent replicas against a staging database. No build or database integration tests were executed here.
