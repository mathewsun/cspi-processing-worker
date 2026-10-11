# Multilingual geographic candidate detection — staging iteration

Adds a small, reviewed alias dictionary for non-English country names and a matching pass against headlines and up to 4,000 characters of article text.

- Sources of aliases: manually curated common country spellings in Russian, Chinese, Arabic, Persian, Hebrew, Hindi, Japanese, Korean, and selected local languages.
- `news_country_aliases` references the authoritative `Countries` table. It is a **starter dictionary**, not coverage of all 195 countries in all languages.
- Explicit mentions are written to `news_country_candidates` with `MultilingualAliasV1`, evidence and a conservative confidence of 0.5.
- Ambiguous aliases are skipped. Geographic classification is **not** inferred from a publisher's home country.
- Nothing is written to `PublicationCountries` or `EventCountries` yet: Primary/Secondary/Mentioned roles still require contextual disambiguation and evidence.
- This is not an embedding model, semantic deduplication or event clustering.

## Required before any run

Apply `sql/004_multilingual_country_aliases.sql` to a **staging** database after earlier processing schema scripts. Build and integration-test first. The current worker now expects the aliases table to exist; deploying code without it will cause processing failures.

Next: add configurable multilingual NER/embedding model, ANN retrieval of candidate events, entity/date checks, explicit role classification, and human-review workflow for low-confidence matches. The current News Worker often supplies only a headline and URL, so full-text acquisition is a prerequisite for reliable analysis.
