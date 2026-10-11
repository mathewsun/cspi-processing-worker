# Country auto decisions: guarded pilot

This PR adds a **disabled-by-default** decision engine, with dry-run as the default when enabled.

Settings:
- `CountryAuto__Enabled=false`
- `CountryAuto__Apply=false` (record decisions only, do not accept)
- `CountryAuto__BatchSize=100`

Apply `sql/012_country_auto_decisions.sql` in a staging DB first. It records Eligible/Skipped/Accepted outcomes. The engine considers only Pending LlmRoleV1 proposals with role Mentioned, reported confidence >= 0.95, and a matching MultilingualAliasV1 mention. It never automatically accepts Primary or Secondary roles. Previously rejected proposals are not overwritten. `CountryRelationsMaterializer` separately handles Accepted proposals.

**Important:** raw LLM confidence is not calibrated and a lexical match is not genuinely independent factual evidence. The current threshold is a pilot eligibility filter, not a guarantee of correctness. Keep Apply=false until a labeled multilingual evaluation and explicit human-correction/reconciliation policy exist. Do not turn this on in production.

Limitations:
- A dry-run Eligible record will not be re-evaluated automatically after Apply changes; a controlled reset/reprocessing workflow is needed.
- No model-version provenance for LlmRoleV1, no evaluation dataset, no automatic revocation of prior geographic links, no country consensus for multi-article events.
- No build or integration tests were run in this environment.
