# Manual geographic correction precedence (staging)

SQL 017 adds persistent manual override markers and two PostgreSQL functions:
- news_set_publication_country_manual(publication_id,country_id,role,actor)
- news_set_event_country_manual(event_id,country_id,role,actor)

Both take the same advisory lock as their respective reconciliation writer, revoke explicit worker ownership, set the relationship Method to Manual, and store a durable manual override marker. The incremental publication and event reconcilers exclude overridden country pairs from new automated inserts.

Roles: 0 Primary, 1 Secondary, 2 Mentioned.

**This is backend infrastructure only.** There is no authenticated CSPI UI/API endpoint in this PR; call the functions only from a trusted, authorized server-side transaction. The database functions themselves do not authenticate the actor string.

Known gaps: manual country removal/exclusion, audit log integration, explicit return-to-automation action, historic manual overrides, ownership backfill, and build/integration tests. Legacy full-scan CountryRelationsMaterializer must stay disabled. Apply SQL 017 only to staging after SQL 015 and 016; production flags remain off.
