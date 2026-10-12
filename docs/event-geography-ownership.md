# Explicit event geography ownership (staging only)

Adds `sql/016_event_geography_ownership.sql` and changes EventGeographyReconciliationService to identify worker-owned EventCountries rows through `news_event_country_ownership`, rather than trusting Method strings.

- New automated EventCountries links receive an ownership record in the same transaction.
- Existing event-country rows are never claimed automatically, even when Method resembles an automated value.
- Only explicitly owned rows may be deleted during reconciliation; deletion cascades to the ownership record.
- Manually created and historical rows are preserved on conflicts.
- The existing incremental dirty-event queue (SQL 013) and conservative country consensus remain unchanged.
- EventGeography__Enabled stays false by default.

## Prerequisites and limitations

Apply SQL 016 to a staging DB after the CSPI multi-country migration and processing SQL 013. No historical ownership backfill is performed. Previously auto-created rows remain unowned until manually audited/migrated. Manual edits to an already worker-owned row require an ownership-release workflow, which is not implemented yet. Full transaction/concurrency tests and build verification are pending. Do not enable in production.
