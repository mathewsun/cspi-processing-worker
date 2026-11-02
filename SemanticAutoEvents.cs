using Npgsql;
namespace Cspi.ProcessingWorker;

// The embedding decision is sufficient for grouping. Qwen is not a prerequisite.
public static class SemanticAutoEvents
{
 public const double Threshold = 0.90;
 public static async Task Promote(NpgsqlConnection db,NpgsqlTransaction tx,CancellationToken ct)
 {
  await using var cmd=LocalSemanticStore.Cmd(db,tx,"""
   INSERT INTO news_event_link_reviews AS r(publication_id_a,publication_id_b,score,evidence,decision,reviewed_at)
   SELECT p.a,p.b,p.similarity,jsonb_build_object('method','LocalSemanticAutoV1','embedding_model',p.model_revision,
     'hash_a',p.hash_a,'hash_b',p.hash_b,'threshold',@threshold,'reason','Automatic semantic grouping'),'Approved',now()
   FROM news_local_semantic_pairs p
   JOIN news_local_semantic_control c ON c.enabled AND c.model_revision=p.model_revision
   JOIN news_local_semantic_vectors va ON va.publication_id=p.a AND va.model_revision=p.model_revision AND va.input_hash=p.hash_a
   JOIN news_local_semantic_vectors vb ON vb.publication_id=p.b AND vb.model_revision=p.model_revision AND vb.input_hash=p.hash_b
   JOIN news_local_semantic_jobs ja ON ja.publication_id=p.a AND ja.model_revision=p.model_revision AND ja.state='Completed' AND ja.dirty_generation=ja.observed_generation
   JOIN news_local_semantic_jobs jb ON jb.publication_id=p.b AND jb.model_revision=p.model_revision AND jb.state='Completed' AND jb.dirty_generation=jb.observed_generation
   WHERE p.similarity>=@threshold AND p.state<>'Invalidated'
    AND coalesce(p.decision,'') NOT IN('different','related_topic','uncertain')
    AND NOT EXISTS(SELECT 1 FROM news_event_pair_blocks x WHERE x.publication_id_a=p.a AND x.publication_id_b=p.b)
    AND NOT EXISTS(SELECT 1 FROM news_event_link_reviews x WHERE x.publication_id_a=p.a AND x.publication_id_b=p.b
      AND (x.decision IN('Approved','Rejected') OR x.evidence->>'human_decision'='true'))
   ORDER BY (ja.queue_kind='Live' OR jb.queue_kind='Live') DESC,p.created_at DESC LIMIT 64
   ON CONFLICT(publication_id_a,publication_id_b) DO UPDATE SET score=excluded.score,evidence=excluded.evidence,
     decision='Approved',reviewed_at=now(),updated_at=now()
   WHERE r.decision='Pending' AND coalesce(r.evidence->>'human_decision','false')<>'true';
   """,("threshold",Threshold));await cmd.ExecuteNonQueryAsync(ct);
 }
}
