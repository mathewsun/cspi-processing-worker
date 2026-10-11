# Experimental country role classification (LLM)

This PR introduces a **disabled-by-default** classifier that generates evidence-backed proposals for `Primary`, `Secondary`, and `Mentioned` roles. It never sets `Accepted` or writes directly to `PublicationCountries` or `EventCountries`.

## Configuration
- `CountryLlm__Enabled=false` (default)
- `CountryLlm__Endpoint=https://your-inference-server/v1/chat/completions`
- `CountryLlm__Model=<model-id>`
- `CountryLlm__ApiKey` supplied via secret

Requires a compatible chat completions endpoint returning JSON in `choices[0].message.content`. Apply `sql/010_llm_country_jobs.sql` in a staging DB, then manually queue a small set of publications for the chosen model ID. This is not automatic mass ingestion.

## Processing rules
- Refuses to classify an article without at least 100 characters of original text.
- Passes only country ISO3 codes from the authoritative Countries table.
- Requires role from Primary/Secondary/Mentioned, confidence in [0,1], and a short evidence quote that occurs verbatim in the article body.
- Limits results to 30 countries, records `LlmRoleV1` proposals as Pending, and marks the job Completed.
- Retries transient failures up to three times with a 15-minute backoff; records failures.
- Does not use the source publisher's home country as a country inference.

## Critical limitations before production
- No distributed lease on `news_country_llm_jobs`: run only one classifier instance on a test DB.
- A matching excerpt is **not sufficient** proof that the role is correct; accuracy requires labeled multilingual evaluation, confidence calibration and disambiguation.
- Model identifier is stored on the job, but the proposals' method is fixed to LlmRoleV1; model provenance/version should be added before accepting classifications.
- No structured JSON schema enforcement at the model endpoint, and strict evidence validation may reject correct paraphrases.
- The current News Worker often saves only titles/URLs, so this classifier will defer most articles until lawful full-text acquisition is available.
- No automatic acceptance or final geographic assignment; the existing CountryRelationsMaterializer only consumes Accepted proposals.
- Provider contracts, privacy, security, prompt-injection resilience, and model/data residency must be evaluated.
- Build and PostgreSQL integration tests have not been run. Do not enable against production.
