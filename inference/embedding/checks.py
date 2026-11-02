import math
from server import Inputs, MODEL_ID, embeddings, detect_language

assert detect_language("") == (0.0, "__label__und")
assert detect_language("\nA body after an empty headline") == (0.0, "__label__und")
assert detect_language("...")[0] >= 0
assert detect_language("Hefði ekki farið frá Gróttu fyrir neitt annað félag")[1] == "__label__is"
request = Inputs(model=MODEL_ID, input=["...", "\nA body after an empty headline", "В Москве открылась новая станция метро.", "The new metro station opened in Moscow."])
result = embeddings(request)
assert len(result["data"]) == 4
for row in result["data"]:
    assert row["language"] and math.isfinite(row["language_confidence"])
    assert len(row["embedding"]) == 384
assert result["data"][1]["language"] == "und"
print("PASS empty and punctuation headlines do not fail the embedding batch")
print("PASS local language detector and multilingual embedding responses")
