import hashlib
import pathlib
import urllib.request

from constants import MODEL_SHA, REVISION
root = pathlib.Path("/model")
root.mkdir(exist_ok=True)
for remote, local in [("onnx/model_quint8_avx2.onnx", "model.onnx"), ("tokenizer.json", "tokenizer.json")]:
    url = f"https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/{REVISION}/{remote}"
    urllib.request.urlretrieve(url, root / local)
assert hashlib.sha256((root / "model.onnx").read_bytes()).hexdigest() == MODEL_SHA, "Model hash mismatch"
