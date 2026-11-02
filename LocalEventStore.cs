using Npgsql;
using System.Text.Json;
namespace Cspi.ProcessingWorker;
public sealed class LocalEventStore(string connection)
{
 const string Revision=LocalSemantic.Revision;
 static NpgsqlCommand Cmd(NpgsqlConnection db,NpgsqlTransaction? tx,string sql,params(string,object?)[] args)=>LocalSemanticStore.Cmd(db,tx,sql,args);
 async Task<NpgsqlConnection> Open(CancellationToken ct){var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);return db;}
 public async Task<EventPair?> Claim(CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,null,"""
   WITH due AS(
    SELECT p.a,p.b FROM news_local_semantic_pairs p
    JOIN news_local_semantic_vectors a ON a.publication_id=p.a AND a.model_revision=p.model_revision AND a.input_hash=p.hash_a
    JOIN news_local_semantic_vectors b ON b.publication_id=p.b AND b.model_revision=p.model_revision AND b.input_hash=p.hash_b
    JOIN news_local_semantic_jobs ja ON ja.publication_id=p.a AND ja.model_revision=p.model_revision AND ja.state='Completed' AND ja.dirty_generation=ja.observed_generation
    JOIN news_local_semantic_jobs jb ON jb.publication_id=p.b AND jb.model_revision=p.model_revision AND jb.state='Completed' AND jb.dirty_generation=jb.observed_generation
    WHERE p.model_revision=@r AND p.state IN('Pending','Failed') AND p.attempts<3 AND p.next_at<=now() AND (p.lease_until IS NULL OR p.lease_until<now())
     AND EXISTS(SELECT 1 FROM news_local_semantic_control WHERE enabled AND model_revision=@r AND next_event_slot<=now())
     AND NOT EXISTS(SELECT 1 FROM news_event_pair_blocks x WHERE x.publication_id_a=p.a AND x.publication_id_b=p.b)
     AND NOT EXISTS(SELECT 1 FROM news_event_link_reviews x WHERE x.publication_id_a=p.a AND x.publication_id_b=p.b AND (x.decision='Rejected' OR (x.decision='Approved' AND (coalesce(x.evidence->>'method','')<>'LocalEventJudgeV1' OR x.evidence->>'human_decision'='true'))))
    ORDER BY p.similarity DESC,p.created_at DESC LIMIT 1 FOR UPDATE OF p SKIP LOCKED
   ), leased AS(UPDATE news_local_semantic_pairs p SET lease_token=@token,lease_until=now()+interval '5 minutes' FROM due WHERE p.a=due.a AND p.b=due.b AND p.model_revision=@r RETURNING p.*)
   SELECT p.a,p.b,p.hash_a,p.hash_b,a.title,a.body,a.published_at,b.title,b.body,b.published_at,p.lease_token FROM leased p
   JOIN news_local_semantic_vectors a ON a.publication_id=p.a AND a.model_revision=p.model_revision JOIN news_local_semantic_vectors b ON b.publication_id=p.b AND b.model_revision=p.model_revision;
   """,("r",Revision),("token",Guid.NewGuid().ToString("N")));
  await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetDateTime(6),r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.GetDateTime(9),r.GetString(10)):null;
 }
 // Called while holding the event-graph advisory lock. Lock parents before jobs
 // to follow the publication-edit trigger order and fence in-flight edits.
 public static async Task<bool> Current(NpgsqlConnection db,NpgsqlTransaction tx,int a,int b,string ha,string hb,CancellationToken ct)
 {
  await using(var parents=Cmd(db,tx,"SELECT \"Id\" FROM \"Publications\" WHERE \"Id\"=ANY(@ids) ORDER BY \"Id\" FOR SHARE",("ids",new[]{a,b})))await parents.ExecuteNonQueryAsync(ct);
  await using var check=Cmd(db,tx,"""
   SELECT v.publication_id FROM news_local_semantic_vectors v JOIN news_local_semantic_jobs j ON j.publication_id=v.publication_id AND j.model_revision=v.model_revision
   JOIN "Publications" p ON p."Id"=v.publication_id LEFT JOIN "Sources" s ON s."Id"=p."SourceId"
   WHERE v.model_revision=@r AND ((v.publication_id=@a AND v.input_hash=@ha) OR (v.publication_id=@b AND v.input_hash=@hb)) AND j.state='Completed' AND j.dirty_generation=j.observed_generation AND coalesce(s."CollectionKind",'')<>'Social'
   ORDER BY v.publication_id FOR SHARE OF j;
   """,("r",Revision),("a",a),("b",b),("ha",ha),("hb",hb));await using var rd=await check.ExecuteReaderAsync(ct);var count=0;while(await rd.ReadAsync(ct))count++;return count==2;
 }
 public async Task<bool> Complete(EventPair pair,EventJudgment judgment,CancellationToken ct)
 {
  var judgeRevision=judgment.Prechecked?"instruction-guard-v1":LocalEventJudge.Revision;
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var graph=Cmd(db,tx,"SELECT pg_advisory_xact_lock(23011,7)"))await graph.ExecuteNonQueryAsync(ct);
  if(!await Current(db,tx,pair.A,pair.B,pair.HashA,pair.HashB,ct))return false;
  await using var update=Cmd(db,tx,"""
   UPDATE news_local_semantic_pairs SET state='Completed',decision=@decision,confidence=@confidence,reason=@reason,judge_revision=@judge,judged_at=now(),lease_token=NULL,lease_until=NULL,attempts=0
   WHERE a=@a AND b=@b AND model_revision=@r AND hash_a=@ha AND hash_b=@hb AND lease_token=@token AND lease_until>clock_timestamp() RETURNING similarity;
   """,("a",pair.A),("b",pair.B),("r",Revision),("ha",pair.HashA),("hb",pair.HashB),("token",pair.Token),("decision",judgment.Decision),("confidence",judgment.Confidence),("reason",judgment.Reason),("judge",judgeRevision));
  var score=await update.ExecuteScalarAsync(ct);if(score is null)return false;
  await using var save=Cmd(db,tx,"""
   INSERT INTO news_local_semantic_judge_audit(a,b,model_revision,hash_a,hash_b,judge_revision,decision,confidence,reason) VALUES(@a,@b,@r,@ha,@hb,@judge,@decision,@confidence,@reason);
   INSERT INTO news_event_link_reviews AS r(publication_id_a,publication_id_b,score,evidence,decision,reviewed_at)
   SELECT @a,@b,@score,@evidence::jsonb,@review,CASE WHEN @review='Approved' THEN now() ELSE NULL END
   WHERE NOT EXISTS(SELECT 1 FROM news_event_pair_blocks WHERE publication_id_a=@a AND publication_id_b=@b)
   ON CONFLICT(publication_id_a,publication_id_b) DO UPDATE SET score=excluded.score,evidence=excluded.evidence,decision=excluded.decision,reviewed_at=excluded.reviewed_at,updated_at=now()
    WHERE coalesce(r.evidence->>'human_decision','false')<>'true'
      AND (r.decision='Pending' OR (r.evidence->>'method'='LocalEventJudgeV1' AND r.decision='Approved'));
   """,("a",pair.A),("b",pair.B),("r",Revision),("ha",pair.HashA),("hb",pair.HashB),("judge",judgeRevision),("decision",judgment.Decision),("confidence",judgment.Confidence),("reason",judgment.Reason),("score",score),
   ("review",judgment.Decision=="same_event"&&judgment.Confidence>=0.90&&!judgment.Contradiction&&!judgment.Prechecked?"Approved":"Pending"),
   ("evidence",JsonSerializer.Serialize(new{method="LocalEventJudgeV1",embedding_model=Revision,judge_model=judgeRevision,hash_a=pair.HashA,hash_b=pair.HashB,relation=judgment.Decision,confidence=judgment.Confidence,reason=judgment.Reason,contradiction=judgment.Contradiction,prechecked=judgment.Prechecked})));
  await save.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return true;
 }
 public async Task Release(EventPair pair,string? error,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,null,"UPDATE news_local_semantic_pairs SET lease_token=NULL,lease_until=NULL,state=CASE WHEN @error='' THEN 'Pending' ELSE 'Failed' END,attempts=attempts+CASE WHEN @error='' THEN 0 ELSE 1 END,next_at=now()+interval '5 minutes',reason=nullif(@error,'') WHERE a=@a AND b=@b AND model_revision=@r AND lease_token=@token",("error",LocalCountryClassifier.Limit(error??"",300)),("a",pair.A),("b",pair.B),("r",Revision),("token",pair.Token));await cmd.ExecuteNonQueryAsync(ct);
 }
 public async Task Cooldown(double seconds,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,null,"UPDATE news_local_semantic_control SET next_event_slot=now()+@seconds*interval '1 second' WHERE id AND model_revision=@r",("seconds",Math.Clamp(seconds,10,3600)),("r",Revision));await cmd.ExecuteNonQueryAsync(ct);
 }
}
