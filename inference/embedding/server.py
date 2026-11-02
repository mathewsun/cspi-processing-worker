import hashlib
import threading
import numpy as np
import onnxruntime as ort
import fasttext
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field
from tokenizers import Tokenizer
from constants import MODEL_SHA, REVISION, LID_SHA

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
assert hashlib.sha256(open("/model/lid.176.ftz", "rb").read()).hexdigest() == LID_SHA
language_model = fasttext.load_model("/model/lid.176.ftz")
app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

def detect_language(text):
    headline = text.split("\n", 1)[0].replace("\r", " ").strip()
    predictions = language_model.f.predict(headline, 1, 0.0, "strict") if headline else []
    return predictions[0] if predictions else (0.0, "__label__und")

class Inputs(BaseModel):
    model: str
    input: list[str] = Field(min_length=1, max_length=16)

@app.get("/health")
def health():
    return {"model": MODEL_ID, "sha256": MODEL_SHA, "revision": REVISION, "tokenizer_sha256": TOKENIZER_SHA, "language_sha256": LID_SHA, "dimensions": 384, "max_tokens": 128}

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
        # Detect from the headline, avoiding publisher boilerplate in the body.
        # Raw pinned fastText API avoids its NumPy 2-incompatible array wrapper.
        languages = [detect_language(s) for s in request.input]
    return {**health(), "data": [{"index": i, "embedding": v.tolist(), "language": languages[i][1].removeprefix("__label__"), "language_confidence": float(languages[i][0])} for i, v in enumerate(vectors)]}
