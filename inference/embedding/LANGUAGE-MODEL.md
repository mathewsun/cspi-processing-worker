Language guard uses Facebook AI Research's compressed fastText lid.176.ftz model
(176 languages, approximately 917 kB), downloaded only at image build time.
Source and license (CC BY-SA 3.0): https://fasttext.cc/docs/en/language-identification.html
SHA256: 8f3472cfe8738a7b6099e8e999c3cbfae0dcd15696aac7d7738a8039db603e83

Automatic cosine grouping requires both headlines' detected languages to be in
the embedding publisher's training coverage and language confidence >=0.75.
Training coverage: https://www.sbert.net/docs/sentence_transformer/pretrained_models.html#multilingual-models
Outside coverage, unrelated headlines can have very high cosine similarity.
These pairs continue through the existing Qwen same-event judgment queue;
neither path needs human confirmation. Human split/rejection overrides both.

Apply sql/020_semantic_language_guard.sql before deploying the new worker.
It requeues previously embedded items without a language decision. It does not
remove existing events or discard archive progress. Deploy the updated embedding
container first; the previous worker ignores the extra response metadata.
