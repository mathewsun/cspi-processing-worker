# Semantic candidate generation (experimental)

This is the first **opt-in** multilingual embedding stage. It does not merge events, delete duplicates, or assign countries. The existing country-alias and title-matching stages continue independently.

## Settings

- `Semantic__Enabled=false` (default; do not enable on production yet)
- `Semantic__Endpoint=https://your-inference-service/v1/embeddings` (OpenAI-compatible embeddings HTTP response)
- `Semantic__Model=<exact-model-id>`
- `Semantic__Dimensions=384` (must match the selected model)
- `Semantic__ApiKey` via a secret, not source control
- `Semantic__SimilarityThreshold=0.88`
- `Semantic__CandidateLimit=250`

Apply `sql/005_semantic_embeddings.sql` to a staging database before enabling. It stores model-versioned vectors and similarity candidates, with a retry record on failures. No GPU or inference service is bundled.

## What it does

1. Finds publications whose exact-deduplication processing is complete and which have no embedding for the configured model.
2. Sends title and up to 4,000 characters of article body to the configured embedding endpoint.
3. Validates vector dimension and finite values, normalizes the vector.
4. Compares with at most 250 same-model embeddings for publications within +/-24 hours.
5. Records cosine-similar candidate pairs without asserting they describe the same event.

## Important limitations

- This implementation scans a bounded set of vectors in application memory. It is **not suitable for 10,000–100,000 publishers at production volume**. Use pgvector/HNSW or a dedicated ANN store before scaling.
- The current queue selection has no distributed lease: do **not** run multiple semantic worker replicas concurrently.
- Model accuracy and threshold need calibration on real multilingual articles. Similarity alone cannot establish shared events; dates, locations, people and facts must agree.
- The current collector often supplies only headlines, reducing quality.
- The configured embedding endpoint may receive copyrighted or personal content: assess provider contracts, data handling, permissions and residency.
- Validate build, schema, HTTPS endpoint, test data, retry behavior and latency before enabling.
- Next: proper distributed semantic job leasing, ANN indexing, evidence-based event clustering and Primary/Secondary/Mentioned country roles.
