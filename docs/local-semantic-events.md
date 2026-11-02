# Local multilingual similarity and event grouping

This replaces the disabled experimental semantic generator with a local CPU
pipeline. It preserves every original publication. Similar topic and same event
are separate results: embeddings produce related-news candidates; the pinned
Qwen3 model validates concrete events before ApprovedEventService groups them.

## Model and resources

Sentence Transformers [paraphrase-multilingual-MiniLM-L12-v2](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2)
at `ae06c001a2546bef168b9bf8f570ccb1a16aaa27`, official AVX2 uint8 ONNX export,
SHA256 `98a01d88b7de996cdea58c32ca71208c09968d143798814b2ea09d3439dc334f`.
Tokenizer SHA256 `2c3387be76557bd40970cec13153b3bbf80407865484b209e655e5e4729076b8`.
Mean pooling with attention mask, 128-token truncation, normalized 384-D vectors.
Revision `minilm-ae06c001-q8-mean128-v1`. Apache-2.0 upstream license.
The private HTTP service has no published host port, remote-code loading or
runtime model downloads. It is capped at 2 CPU / 1 GiB. Initial five-headline
CPU probe: about 418 MiB resident memory and 45 ms per request (not a production
throughput guarantee). Locked Python dependencies and base-image digest are kept.

The existing Qwen3 4B alias/digest is unchanged. Event judgments share its one
inference stream with countries and comments; a persisted 9x-duration cooldown
reserves approximately 10% wall time for event checks. Comments retain their
own roughly 10% share; countries use the remaining slots. No second Qwen copy,
paid endpoint, GPU or provider credential is introduced.

## Durable pipeline

Apply SQL 019 after installing pgvector 0.8.1. SQL 001/003/005/006/007/008/013/016
are prerequisites. The app image contains the same PostgreSQL 17.10 binaries
as production plus the extension, without a major-version/volume-format change.

Initialization locks publication writes briefly while snapshot max ID and live
trigger ownership switch atomically. A descending durable archive cursor enqueues
2,000 IDs at a time; new/edited publications enqueue transactionally. Live work
gets four preferred batches then one archive batch, with fallback. Social posts
are excluded. Claim batches of 16 have fenced five-minute leases and three-failure
limits. Text scope: title up to 600 UTF-16 units and first 900 body units, then
tokenizer truncation. Hashes include this exact input, publication timestamp and
source ID. Empty titles are skipped. Pausing collection does not erase stored
news or their analysis.

Completion locks the publication before its job, checks generation, token,
expiry and current hash. A transaction advisory lock serializes vector commits,
so concurrently embedded pairs cannot miss each other. The model/tokenizer
digests, output count, indices, finite values and unit norms are validated.
No fabricated vectors/results are saved after an error.

[pgvector](https://github.com/pgvector/pgvector) HNSW/cosine search retrieves at
most 64 candidates within +/-48 publisher hours with iterative filtered scans
capped at 20,000 tuples. Up to eight neighbors scoring >=0.72 are retained;
the top three scoring >=0.80 enter the Qwen queue. These are initial operational
thresholds, not calibrated probabilities. ANN is approximate and may miss pairs.
There is no exhaustive quadratic archive comparison. Current candidates also
appear in the existing semantic-candidate view.

Qwen returns same_event / related_topic / different / uncertain, confidence and
brief evidence. Only same_event >=0.90 proposes automatic approval. Others can
remain pending for an optional reviewer decision; no manual approval is required
for high-confidence same-event results. Its confidence is uncalibrated.

Edited inputs invalidate old candidate decisions and leases. New answers require
current vector hashes and completed current-generation jobs. Already saved event
groups remain auditable and are flagged for review after their input changes;
they are not silently rewritten. Automatic approval is rechecked at materialization.
All original texts, vectors' input scope and model decisions/audit remain stored.

## Operator precedence / graph safety

Manual review marks human_decision in evidence. Model retries never overwrite a
human approval/rejection. Manual splits create pair blocks. All materialization,
model event approval and splits share graph lock (23011,7). Cross-cluster blocks
and rejections are checked before adding a new member, including indirect links
through another member. Existing multi-member groups are not merged blindly;
conflicts are recorded. Provisional singleton rules remain unchanged.
Local semantic commits use separate lock (23011,12); source edits are fenced in
publication -> job order. Review/correction endpoints retain roles and CSRF.

## Deployment and observation

Back up the full database. Build the pinned PostgreSQL extension image, embedding
image, worker and app. Briefly stop app/collectors/worker, recreate only postgres
on the existing named volume, apply SQL 019, start the new app/model/worker and
restart unchanged collector containers. Append compose.semantic-ai.yaml after all
current social/country/discussion overrides. Never remove volumes or print keys.

Enable LocalSemantic / LocalEvent / ApprovedEvents / EventGeography; keep legacy
Semantic / EventReview / AutoEvents and SingletonEvents disabled. Worker rejects
mixed old/new writers or an unpinned Qwen digest. Country/event geography remains
based on accepted content roles, not publisher home country; manual roles win.

`/Semantic` shows progress and recent groups to reviewers/admins. Article cards
show semantic neighbors and saved event membership. The existing event card has
all member articles and the split action. Model candidates remain distinct from
confirmed groups. Queues and audit live in news_local_semantic_* tables.

Tests: real pgvector migrations, archive/live enqueue, leases/checkpoints, stale
input fencing, idempotent grouping, cross-cluster manual rejection, cooldown,
actual MVC/Razor roles/escaping and CSRF-protected split. SEMANTIC_LIVE_CHECK=1
uses the real embedding model and Qwen on synthetic translations, different
places/actions and hostile instructions. Manually labeled real-news evaluation
and capacity tuning remain necessary as source volume grows.
