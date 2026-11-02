# Local Qwen3 country classification

`LocalCountry:Enabled=true` activates a durable queue and one inference stream.
Apply SQL 018 after 017. Use the same pinned model and prompt revision for the
whole run; startup rejects a revision reused with different metadata. Ollama is
internal at `http://country-model:11434/`; no host port or API key is required.
The worker verifies the full expected model digest before claiming each job.

All `Publications` existing at initialization form the archive snapshot. IDs are
enqueued in transactions of 10,000 with a persistent cursor; all later IDs enter
the Live queue without depending on semantic processing or minimum body length.
An insert trigger writes each new job in the ingestion transaction: a lower ID
committed after the cursor passes it cannot be skipped. Initializing a new run
briefly takes a SHARE table lock to include all pre-run ingestion transactions in
its snapshot. Reapplying SQL 018 repairs any missing jobs before trigger cutover.
Claims allocate four slots to Live, one to Archive, with fallback to the other
queue. Each queue is FIFO by publication ID. There is one active local classifier
across instances/revisions (Postgres session advisory lock 23011/10).

The request supplies **all country names**, including catalog rows without ISO3.
An exact catalog name maps back to its database ID in code. Russia's Russian
display name maps to `Russia` for the model only. Numeric IDs in prompts were
rejected after full-catalog checks demonstrated incorrect ID selection. The
article is untrusted data and includes its bounded title and lead (up to 400 +
2,600 characters, twelve fragments of at most 400 characters). No publisher
country, legacy default country, or source coverage enters inference. The
schema constrains countries/roles/evidence indices. Every result is validated;
duplicates use the strongest supported role. Empty output is an abstention,
not a Russian/default assignment. Title-only articles remain explicitly labeled.

Model relations are automatically Accepted and materialized incrementally as
`Qwen3CountryV1`. Confidence 0.5 is a neutral placeholder, **not a calibrated
probability**. `CountryLlm`, `CountryRoles`, `CountryAuto` and the legacy full-scan
materializer must remain disabled; `PublicationGeography` must be enabled.
Manual override/exclusion markers and rejected proposals veto the corresponding
model relation. Materialization never takes ownership of existing/manual links.

Inference runs outside database transactions. Claims have a fresh random token
and five-minute lease; HTTP timeout is 180 seconds. Commit checks the current
input hash, token and unexpired lease under a publication read lock. The same
geography advisory lock as operator corrections serializes updates. Invalid,
truncated or failed responses retry after fifteen minutes, at most three times;
they never become an empty successful classification. Results retain model run,
input hash, scope, evidence and timestamps in `news_country_ai_results`.

An actual title/body change invalidates an in-flight lease and requeues the job,
including completed/failed jobs. The next result replaces only this model's
non-rejected proposals. Manual links and exclusions survive. Historical input
results are retained. Published time does not define ingestion order or urgency.

The web app exposes `/PublicationGeography/Queue` and an editor linked from each
news detail. Operator, administrator and reviewer roles can correct links;
mutations require CSRF and store an atomic audit entry. Removing a link writes
a persistent exclusion; setting a role replaces that exclusion with a manual link.

## Verification

`checks/CountryAiChecks.csproj` runs against the dedicated `country_ai_checks`
database and applies the real SQL files. It verifies enqueue idempotency, FIFO /
archive slots, title-only input, schema validation, duplicate normalization,
automatic materialization/provenance, manual vetoes, independent country edits,
input enrichment, stale/expired lease fencing, retries and result history.
`COUNTRY_AI_LIVE_FIXTURE` instead runs the same production client/parser against
Ollama with a JSON fixture containing `Countries` and `Articles`/expected IDs.
The app's `tests/GeographyUiChecks` uses these SQL files and tests real
authentication, role restrictions, Razor encoding, CSRF and audited edits.

## Operations and rollback

Inspect counts by model_id/state/queue_kind in `news_country_llm_jobs`, failures
after attempts >= 3, and `news_country_ai_runs` cursors. A completed job may have
zero countries or preserve manual corrections. A queue count is not the number
of confirmed Russian stories. On this CPU, arrivals can outpace inference; no
completion deadline is promised for the roughly one-million-article archive.

To pause, disable `LocalCountry:Enabled` and recreate only processing-worker;
keep legacy country writers disabled. Queue/cursors/audit remain resumable.
Disabling the model does not stop ingestion. Do not delete the Ollama volume or
truncate geography tables. Before deployment, back up geographic metadata.
If reverting automatic links, delete only worker-owned `Qwen3CountryV1` links
under the geography lock after reviewing the backup; never delete Manual rows.
