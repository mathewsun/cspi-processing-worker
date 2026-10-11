# Approved event materialization (staging)

This stage creates CSPI Events and EventPublications **only for explicitly Approved review pairs**. It does not auto-approve candidate similarity matches, and does not infer country roles.

## Configuration

`ApprovedEvents__Enabled=false` by default. Apply `sql/007_event_review_applications.sql` to a test database after previous processing scripts and the CSPI multi-country migration. Do not enable on production until build/integration tests and reviewer UI are ready.

## Safety behavior

- PostgreSQL transaction advisory lock serializes graph modifications across replicas.
- If neither publication belongs to an event, create one event and link both.
- If exactly one belongs to an event, link the other.
- If both belong to **different** events, record Conflict and do not merge clusters.
- Record each applied pair idempotently.
- Legacy Event.Country is set to Unclassified; no EventCountries or PublicationCountries are written.

## Important limitations

- ExistingEvent currently picks the first EventPublications match. If a publication already belongs to multiple events, manual review is required; add a multi-membership conflict check before production.
- The review decision is expected to come from a trusted human or separately validated approval workflow. There is no reviewer UI in this PR.
- Candidate evidence alone cannot establish that articles describe the same event.
- Changing an Approved decision after application does not automatically undo links.
- Event naming uses one publication title and does not summarize a cluster.
- Full country role classification and provenance remain future work.
- Build and database integration tests have not been executed.
