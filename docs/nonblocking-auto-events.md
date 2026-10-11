# Non-blocking event automation — conservative pilot

This is an opt-in foundation, not a calibrated autonomous event classifier.

- AutoEvents__Enabled=false (default).
- AutoEvents__SemanticMin=0.96 and AutoEvents__TitleMin=0.90 are raw similarity thresholds, **not probabilities**.
- A pair must pass BOTH independent similarity signals, be from different sources, and have timestamps within 12 hours.
- Human-reviewed pairs are never overwritten; manually blocked pairs are excluded.
- Qualifying pairs are marked Approved in the existing review queue with method AutoDualEvidenceV1.
- The existing ApprovedEventService materializes them ONLY if ApprovedEvents__Enabled=true, and is separately disabled by default.
- Other pairs remain as independent publications; nobody has to press Approve for ingestion to continue.
- Do not automatically merge two already-distinct event clusters.

## Correction support

SQL 008 creates news_event_pair_blocks and news_event_actions. The CSPI correction UI can detach a publication from an event and block relinking with all remaining members. Corrections are durable. No physical article deletion.

## Before production

This does not perform factual consistency checking, calibrated confidence estimation, multilingual role classification, or automatic singleton event creation. Similarity thresholds must be calibrated with labeled real news. Event splitting currently detaches a publication but does not create a new event for it. Manual event-to-event merging and full undo UI are future work.

Apply SQL 008 to staging before running either new component. Test the event graph, race conditions, audit, migrations, and load first. Both switches stay disabled until tests pass.
