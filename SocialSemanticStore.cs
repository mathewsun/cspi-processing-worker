using Npgsql;
using System.Text.Json;
namespace Cspi.ProcessingWorker;

public sealed record SemanticPost(long Id,string Body,string Hash,DateTime Published,string Token) { public string InputKey=>Hash+":"+Published.ToUniversalTime().ToString("O"); }
public sealed record SocialSemanticMatch(SemanticArticle Article,double Similarity);
public sealed class SocialSemanticStore(string connection)
{
 public const string Actor="semantic-social-v1";
 public const string Revision=LocalSemantic.Revision+"+"+LocalEventJudge.Revision+"+social-v1";
 static NpgsqlCommand Cmd(NpgsqlConnection db,NpgsqlTransaction? tx,string sql,params(string,object?)[] args)=>LocalSemanticStore.Cmd(db,tx,sql,args);
 async Task<NpgsqlConnection> Open(CancellationToken ct){var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);return db;}
 public async Task<SemanticPost?> Claim(bool archive,CancellationToken ct)
 {
  await using var db=await Open(ct);
  await using var cmd=Cmd(db,null,"""
   WITH due AS(SELECT j.post_id FROM social_semantic_jobs j JOIN social_posts p ON p.id=j.post_id JOIN social_channels c ON c.id=p.channel_id
    WHERE j.next_at<=now() AND (j.lease_until IS NULL OR j.lease_until<now()) AND j.attempts<3 AND NOT p.deleted AND c.enabled
    AND p.publication_id IS NOT NULL AND p.published_at IS NOT NULL AND length(btrim(p.body))>=30
    AND EXISTS(SELECT 1 FROM news_local_semantic_control WHERE enabled AND model_revision=@revision AND next_event_slot<=now())
    ORDER BY (j.queue_kind=CASE WHEN @archive THEN 'Archive' ELSE 'Live' END) DESC,j.next_at,CASE WHEN @archive THEN p.id END ASC,p.id DESC FOR UPDATE OF j SKIP LOCKED LIMIT 1),
   leased AS(UPDATE social_semantic_jobs j SET lease_token=@token,lease_until=now()+interval '5 minutes' FROM due WHERE j.post_id=due.post_id RETURNING j.*)
   SELECT p.id,p.body,p.body_hash,p.published_at,j.lease_token FROM leased j JOIN social_posts p ON p.id=j.post_id;
   """,("revision",LocalSemantic.Revision),("archive",archive),("token",Guid.NewGuid().ToString("N")));
  await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetDateTime(3),r.GetString(4)):null;
 }
 public async Task<SocialSemanticMatch?> Nearest(SemanticPost post,float[] vector,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var settings=Cmd(db,tx,"SET LOCAL statement_timeout='20s'; SET LOCAL hnsw.ef_search=100; SET LOCAL hnsw.iterative_scan=strict_order; SET LOCAL hnsw.max_scan_tuples=20000"))await settings.ExecuteNonQueryAsync(ct);
  await using var cmd=Cmd(db,tx,"""
   WITH near AS MATERIALIZED(SELECT v.*,v.embedding<=>@vector::vector AS distance FROM news_local_semantic_vectors v
    WHERE model_revision=@embedding AND published_at BETWEEN @from AND @to ORDER BY v.embedding<=>@vector::vector LIMIT 256)
   SELECT v.publication_id,v.title,v.body,v.published_at,v.source_id,v.input_hash,1-v.distance
   FROM near v JOIN news_local_semantic_jobs j ON j.publication_id=v.publication_id AND j.model_revision=v.model_revision
   JOIN "Publications" p ON p."Id"=v.publication_id LEFT JOIN "Sources" s ON s."Id"=p."SourceId"
   WHERE j.state='Completed' AND j.dirty_generation=j.observed_generation AND coalesce(s."CollectionKind",'')<>'Social'
    AND 1-v.distance>=0.80 AND v.language=ANY(@languages) AND v.language_confidence>=0.75
    AND NOT EXISTS(SELECT 1 FROM social_article_links l WHERE l.post_id=@post AND l.state IN('AutoConfirmed','OperatorConfirmed'))
    AND NOT EXISTS(SELECT 1 FROM social_article_links l WHERE l.post_id=@post AND l.publication_id=v.publication_id AND l.state='Rejected')
    AND NOT EXISTS(SELECT 1 FROM social_semantic_decisions d WHERE d.post_id=@post AND d.publication_id=v.publication_id AND d.post_hash=@hash AND d.article_hash=v.input_hash AND d.model_revision=@revision AND (d.decision<>'same_event' OR d.confidence<0.90))
   ORDER BY v.distance LIMIT 1;
   """,("vector",LocalSemantic.Vector(vector)),("embedding",LocalSemantic.Revision),("from",post.Published.AddHours(-48)),("to",post.Published.AddHours(48)),("languages",SemanticAutoEvents.SupportedLanguages.ToArray()),("post",post.Id),("hash",post.InputKey),("revision",Revision));
  await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
  var article=new SemanticArticle(r.GetInt32(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetDateTime(3),r.IsDBNull(4)?null:r.GetInt32(4),0,"");
  if(article.Hash!=r.GetString(5))throw new InvalidDataException("Stored article hash mismatch");
  return new(article,r.GetDouble(6));
 }
 public async Task<bool> Complete(SemanticPost post,SocialSemanticMatch? match,EventJudgment? judgment,string result,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var timeout=Cmd(db,tx,"SET LOCAL statement_timeout='20s'"))await timeout.ExecuteNonQueryAsync(ct);
  // Parent rows precede queue rows, as in the ingestion triggers. Never hold a lease-row lock during inference.
  await using(var current=Cmd(db,tx,"SELECT body_hash FROM social_posts WHERE id=@post AND NOT deleted AND published_at=@date FOR SHARE",("post",post.Id),("date",post.Published)))
   if(await current.ExecuteScalarAsync(ct) as string!=post.Hash)return false;
  if(match is not null)
  {
   await using(var parent=Cmd(db,tx,"SELECT \"Id\" FROM \"Publications\" WHERE \"Id\"=@id FOR SHARE",("id",match.Article.Id)))await parent.ExecuteScalarAsync(ct);
   await using var current=Cmd(db,tx,"""
    SELECT v.input_hash FROM news_local_semantic_vectors v JOIN news_local_semantic_jobs j USING(publication_id,model_revision)
    JOIN "Publications" p ON p."Id"=v.publication_id LEFT JOIN "Sources" s ON s."Id"=p."SourceId"
    WHERE v.publication_id=@id AND v.model_revision=@revision AND j.state='Completed' AND j.dirty_generation=j.observed_generation
     AND coalesce(s."CollectionKind",'')<>'Social' FOR SHARE OF j;
    """,("id",match.Article.Id),("revision",LocalSemantic.Revision));
   if(await current.ExecuteScalarAsync(ct) as string!=match.Article.Hash)return false;
  }
  await using(var gate=Cmd(db,tx,"SELECT pg_advisory_xact_lock(23011,11)"))await gate.ExecuteNonQueryAsync(ct);
  await using(var finish=Cmd(db,tx,"""
   UPDATE social_semantic_jobs j SET lease_token=NULL,lease_until=NULL,processed_at=now(),attempts=0,last_result=@result,
    next_at=now()+CASE WHEN @more THEN interval '1 minute' ELSE interval '1 hour' END
   WHERE post_id=@post AND lease_token=@token AND lease_until>clock_timestamp()
    AND EXISTS(SELECT 1 FROM social_posts p JOIN social_channels c ON c.id=p.channel_id WHERE p.id=j.post_id AND c.enabled)
    AND EXISTS(SELECT 1 FROM news_local_semantic_control WHERE enabled AND model_revision=@revision) RETURNING post_id;
   """,("post",post.Id),("token",post.Token),("result",result),("more",match is not null),("revision",LocalSemantic.Revision)))
   if(await finish.ExecuteScalarAsync(ct) is null)return false;
  if(match is not null && judgment is not null)
  {
   var accepted=judgment.Decision=="same_event"&&judgment.Confidence>=0.90&&!judgment.Contradiction&&!judgment.Prechecked;
   var evidence=JsonSerializer.Serialize(new{method=Actor,embedding_model=LocalSemantic.Revision,judge_model=LocalEventJudge.Revision,post_hash=post.Hash,article_hash=match.Article.Hash,similarity=match.Similarity,decision=judgment.Decision,confidence=judgment.Confidence,reason=judgment.Reason});
   await using var save=Cmd(db,tx,"""
    INSERT INTO social_semantic_decisions(post_id,publication_id,post_hash,article_hash,model_revision,decision,confidence,reason,similarity)
    VALUES(@post,@id,@ph,@ah,@revision,@decision,@confidence,@reason,@similarity) ON CONFLICT DO NOTHING;
    WITH previous AS(SELECT state FROM social_article_links WHERE publication_id=@id AND post_id=@post),
    saved AS(INSERT INTO social_article_links AS l(publication_id,post_id,state,evidence,changed_by)
     SELECT @id,@post,'AutoConfirmed',@evidence,@actor WHERE @accepted
      AND NOT EXISTS(SELECT 1 FROM social_article_links WHERE post_id=@post AND state IN('AutoConfirmed','OperatorConfirmed'))
     ON CONFLICT(publication_id,post_id) DO UPDATE SET state='AutoConfirmed',evidence=excluded.evidence,changed_by=excluded.changed_by,changed_at=now()
      WHERE l.state='AutoRemoved'
     RETURNING publication_id,post_id)
    INSERT INTO social_article_link_audit(publication_id,post_id,previous_state,new_state,evidence,actor)
     SELECT publication_id,post_id,(SELECT state FROM previous),'AutoConfirmed',@evidence,@actor FROM saved;
    """,("post",post.Id),("id",match.Article.Id),("ph",post.InputKey),("ah",match.Article.Hash),("revision",Revision),("decision",judgment.Decision),("confidence",judgment.Confidence),("reason",judgment.Reason),("similarity",match.Similarity),("accepted",accepted),("evidence",evidence),("actor",Actor));
   await save.ExecuteNonQueryAsync(ct);
  }
  await tx.CommitAsync(ct);return true;
 }
 public async Task Release(SemanticPost post,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,null,"UPDATE social_semantic_jobs SET lease_token=NULL,lease_until=NULL,attempts=attempts+1,next_at=now()+interval '5 minutes',last_result='Retry' WHERE post_id=@post AND lease_token=@token",("post",post.Id),("token",post.Token));await cmd.ExecuteNonQueryAsync(ct);
 }
}
