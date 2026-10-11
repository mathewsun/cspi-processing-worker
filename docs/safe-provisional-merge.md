# Safe provisional event merge — first increment

The existing ApprovedEventService now attempts to fold a **single untouched provisional event** into another event when an Approved pair connects two event IDs.

## Rules
- Uses the same PostgreSQL advisory transaction lock as singleton creation and manual split.
- Source event must contain exactly one link with method ProvisionalSingletonV1.
- No EventCountries or legacy Publications.EventId references on the source event.
- No manual pair block or Rejected review between source and target members.
- Source event record is retained (empty), and news_event_actions records the move.
- If safety checks fail, records Conflict and does not merge.
- Does not change the approval decision or infer countries.

## Known gaps / do not deploy yet
- This is not a general event-cluster merger: multi-article clusters remain separate.
- Does not provide a user-facing undo action; audit history alone is not a rollback mechanism.
- No validation of full factual agreement; the existing approval stage still uses raw similarity signals.
- Multi-event membership and concurrent manual changes need PostgreSQL integration tests.
- An empty source Event remains in the database and should be hidden/archived in a later iteration.
- Schema SQL 008 from the previous PR must exist; no new schema in this PR.
- Build and integration tests have not been executed. Leave automatic linking disabled on production.
