using Npgsql;
namespace Cspi.ProcessingWorker;

// The embedding decision is sufficient for grouping. Qwen is not a prerequisite.
public static class SemanticAutoEvents
{
 public const double Threshold = 0.90;
 // Model publisher's training-language coverage (regional variants normalized by fastText).
 public static readonly string[] SupportedLanguages="ar bg ca cs da de el en es et fa fi fr gl gu he hi hr hu hy id it ja ka ko ku lt lv mk mn mr ms my nb no nl pl pt ro ru sk sl sq sr sv th tr uk ur vi zh".Split(' ');
 public static async Task Promote(NpgsqlConnection db,NpgsqlTransaction tx,CancellationToken ct)
 {
  await using var cmd=LocalSemanticStore.Cmd(db,tx,"""
   INSERT INTO news_event_link_reviews AS r(publication_id_a,publication_id_b,score,evidence,decision,reviewed_at)
   SELECT p.a,p.b,p.similarity,jsonb_build_object('method','LocalSemanticAutoV1','embedding_model',p.model_revision,
     'hash_a',p.hash_a,'hash_b',p.hash_b,'threshold',@threshold,'language_a',va.language,'language_b',vb.language,'language_model',@lid,'reason','Automatic semantic grouping'),'Approved',now()
   FROM news_local_semantic_pairs p
   JOIN news_local_semantic_control c ON c.enabled AND c.model_revision=p.model_revision
   JOIN news_local_semantic_vectors va ON va.publication_id=p.a AND va.model_revision=p.model_revision AND va.input_hash=p.hash_a
   JOIN news_local_semantic_vectors vb ON vb.publication_id=p.b AND vb.model_revision=p.model_revision AND vb.input_hash=p.hash_b
   JOIN news_local_semantic_jobs ja ON ja.publication_id=p.a AND ja.model_revision=p.model_revision AND ja.state='Completed' AND ja.dirty_generation=ja.observed_generation
   JOIN news_local_semantic_jobs jb ON jb.publication_id=p.b AND jb.model_revision=p.model_revision AND jb.state='Completed' AND jb.dirty_generation=jb.observed_generation
   WHERE p.similarity>=@threshold AND p.state<>'Invalidated'
    AND va.language=ANY(@languages) AND vb.language=ANY(@languages) AND va.language_confidence>=0.75 AND vb.language_confidence>=0.75
    AND coalesce(p.decision,'') NOT IN('different','related_topic','uncertain')
    AND NOT EXISTS(SELECT 1 FROM news_event_pair_blocks x WHERE x.publication_id_a=p.a AND x.publication_id_b=p.b)
    AND NOT EXISTS(SELECT 1 FROM news_event_link_reviews x WHERE x.publication_id_a=p.a AND x.publication_id_b=p.b
      AND (x.decision='Rejected' OR x.evidence->>'human_decision'='true' OR (x.decision='Approved' AND NOT(
        coalesce(x.evidence->>'method','')='LocalSemanticAutoV1' AND coalesce(x.evidence->>'human_decision','false')<>'true'
        AND (x.evidence->>'hash_a' IS DISTINCT FROM p.hash_a OR x.evidence->>'hash_b' IS DISTINCT FROM p.hash_b)
        AND NOT EXISTS(SELECT 1 FROM news_event_review_applications app WHERE app.publication_id_a=p.a AND app.publication_id_b=p.b)))))
   ORDER BY (ja.queue_kind='Live' OR jb.queue_kind='Live') DESC,p.created_at DESC LIMIT 64
   ON CONFLICT(publication_id_a,publication_id_b) DO UPDATE SET score=excluded.score,evidence=excluded.evidence,
     decision='Approved',reviewed_at=now(),updated_at=now()
   WHERE coalesce(r.evidence->>'human_decision','false')<>'true' AND (r.decision='Pending' OR
     (r.decision='Approved' AND r.evidence->>'method'='LocalSemanticAutoV1'
      AND NOT EXISTS(SELECT 1 FROM news_event_review_applications app WHERE app.publication_id_a=r.publication_id_a AND app.publication_id_b=r.publication_id_b)));
   """,("threshold",Threshold),("languages",SupportedLanguages),("lid",LocalSemantic.LanguageSha));await cmd.ExecuteNonQueryAsync(ct);
 }
}
