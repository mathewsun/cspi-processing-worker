import hashlib
import threading
import numpy as np
import onnxruntime as ort
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field
from tokenizers import Tokenizer
from constants import MODEL_SHA, REVISION

# No downloads, remote code or model fallback at runtime.
MODEL_ID = "minilm-ae06c001-q8-mean128-v1"
assert hashlib.sha256(open("/model/model.onnx", "rb").read()).hexdigest() == MODEL_SHA
TOKENIZER_SHA = hashlib.sha256(open("/model/tokenizer.json", "rb").read()).hexdigest()
tokenizer = Tokenizer.from_file("/model/tokenizer.json")
pad_id = tokenizer.token_to_id("<pad>")
assert pad_id is not None
tokenizer.enable_truncation(max_length=128)
tokenizer.enable_padding(pad_id=pad_id, pad_token="<pad>")
options = ort.SessionOptions()
options.intra_op_num_threads = 2
options.inter_op_num_threads = 1
session = ort.InferenceSession("/model/model.onnx", sess_options=options, providers=["CPUExecutionProvider"])
lock = threading.Lock()
app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

class Inputs(BaseModel):
    model: str
    input: list[str] = Field(min_length=1, max_length=16)

@app.get("/health")
def health():
    return {"model": MODEL_ID, "sha256": MODEL_SHA, "revision": REVISION, "tokenizer_sha256": TOKENIZER_SHA, "dimensions": 384, "max_tokens": 128}

@app.post("/v1/embeddings")
def embeddings(request: Inputs):
    if request.model != MODEL_ID or any(not s.strip() or len(s) > 1600 for s in request.input):
        raise HTTPException(422, "Invalid model or input")
    with lock:
        encoded = tokenizer.encode_batch(request.input)
        feeds = {
            "input_ids": np.array([e.ids for e in encoded], dtype=np.int64),
            "attention_mask": np.array([e.attention_mask for e in encoded], dtype=np.int64),
            "token_type_ids": np.array([e.type_ids for e in encoded], dtype=np.int64),
        }
        output = session.run(None, {i.name: feeds[i.name] for i in session.get_inputs()})[0]
        mask = feeds["attention_mask"].astype(np.float32)[..., None]
        if output.ndim != 3 or output.shape[2] != 384:
            raise HTTPException(500, "Unexpected model output")
        vectors = (output * mask).sum(axis=1) / np.maximum(mask.sum(axis=1), 1)
        norms = np.linalg.norm(vectors, axis=1, keepdims=True)
        if not np.isfinite(vectors).all() or (norms < 1e-8).any():
            raise HTTPException(500, "Invalid embedding")
        vectors = vectors / norms
    return {**health(), "data": [{"index": i, "embedding": v.tolist()} for i, v in enumerate(vectors)]}
