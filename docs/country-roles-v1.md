# Country-role classification — conservative first stage

This iteration prepares evidence-backed country-role proposals without treating a publisher's country as the article's country.

- `CountryRoles__Enabled=false` by default.
- Apply `sql/009_country_role_proposals.sql` in a staging database after prior scripts.
- A direct country-name mention is proposed as `Mentioned`, with confidence 0.5 and stored evidence.
- `Primary` and `Secondary` are deliberately not inferred from mentions alone. They require factual entity/relation analysis.
- Proposals do **not** populate `PublicationCountries` or `EventCountries`; this avoids falsely presenting unreviewed candidates as geographic facts.
- Source country is not used to classify an article.

## Next stages
1. Evaluate multilingual named-entity recognition and country disambiguation.
2. Add a structured factual role classifier for Primary/Secondary/Mentioned, with evidence, model version, confidence calibration and abstention.
3. Write final publication-country relationships only after a validated decision; aggregate event countries from constituent publications, preserving conflicting evidence.
4. Build integration tests and evaluate precision/recall on manually labeled cross-border news.
5. Add idempotent incremental queue and horizontal leases before scaling.

**Not production-ready.** No build, migration or integration tests have been executed.
