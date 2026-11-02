using Npgsql;
namespace Cspi.ProcessingWorker;
public sealed class LocalSemanticStore(string connection)
{
 const string Revision=LocalSemantic.Revision;
 internal static NpgsqlCommand Cmd(NpgsqlConnection db,NpgsqlTransaction? tx,string sql,params(string,object?)[] args)
 {var c=new NpgsqlCommand(sql,db,tx);foreach(var(k,v)in args){if(v is null)c.Parameters.Add(new NpgsqlParameter(k,NpgsqlTypes.NpgsqlDbType.Unknown){Value=DBNull.Value});else c.Parameters.AddWithValue(k,v);}return c;}
 async Task<NpgsqlConnection> Open(CancellationToken ct){var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);return db;}
 public async Task Initialize(CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var check=Cmd(db,tx,"SELECT EXISTS(SELECT 1 FROM news_local_semantic_control WHERE enabled AND model_revision=@r AND model_sha=@sha)",("r",Revision),("sha",LocalSemantic.Sha)))if(await check.ExecuteScalarAsync(ct) is true)return;
  await using var setup=Cmd(db,tx,"""
   LOCK TABLE "Publications" IN SHARE ROW EXCLUSIVE MODE;
   INSERT INTO news_local_semantic_runs(model_revision,snapshot_max_id,archive_cursor) SELECT @r,coalesce(max("Id"),0),coalesce(max("Id"),0)::bigint+1 FROM "Publications" ON CONFLICT DO NOTHING;
   INSERT INTO news_local_semantic_control(id,enabled,model_revision,model_sha) VALUES(true,true,@r,@sha) ON CONFLICT(id) DO UPDATE SET enabled=true,model_revision=@r,model_sha=@sha;
   """,("r",Revision),("sha",LocalSemantic.Sha));await setup.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
 }
 public async Task<int> Enqueue(CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var cmd=Cmd(db,tx,"""
   WITH run AS(SELECT archive_cursor FROM news_local_semantic_runs WHERE model_revision=@r FOR UPDATE),
   batch AS MATERIALIZED(SELECT p."Id",p."SourceId",p."Title" FROM "Publications" p,run WHERE p."Id"<run.archive_cursor ORDER BY p."Id" DESC LIMIT 2000),
   saved AS(INSERT INTO news_local_semantic_jobs(publication_id,model_revision,queue_kind)
    SELECT b."Id",@r,'Archive' FROM batch b LEFT JOIN "Sources" s ON s."Id"=b."SourceId" WHERE coalesce(s."CollectionKind",'')<>'Social' AND btrim(b."Title")<>''
    ON CONFLICT DO NOTHING RETURNING publication_id),
   advanced AS(UPDATE news_local_semantic_runs SET archive_cursor=coalesce((SELECT min("Id") FROM batch),0) WHERE model_revision=@r RETURNING archive_cursor)
   SELECT count(*) FROM saved;
   """,("r",Revision));var count=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));await tx.CommitAsync(ct);return count;
 }
 public async Task<List<SemanticArticle>> Claim(bool archive,CancellationToken ct)
 {
  await using var db=await Open(ct);var list=new List<SemanticArticle>();
  await using var cmd=Cmd(db,null,"""
   WITH due AS(
    SELECT j.publication_id FROM news_local_semantic_jobs j JOIN "Publications" p ON p."Id"=j.publication_id LEFT JOIN "Sources" s ON s."Id"=p."SourceId"
    WHERE j.model_revision=@r AND j.state IN('Pending','Failed') AND j.attempts<3 AND j.next_at<=now() AND (j.lease_until IS NULL OR j.lease_until<now())
     AND coalesce(s."CollectionKind",'')<>'Social' AND btrim(p."Title")<>'' AND EXISTS(SELECT 1 FROM news_local_semantic_control WHERE enabled AND model_revision=@r)
    ORDER BY (j.queue_kind=CASE WHEN @archive THEN 'Archive' ELSE 'Live' END) DESC,j.publication_id DESC FOR UPDATE OF j SKIP LOCKED LIMIT 16
   ), leased AS(UPDATE news_local_semantic_jobs j SET lease_token=@token,lease_until=now()+interval '5 minutes' FROM due WHERE j.publication_id=due.publication_id AND j.model_revision=@r RETURNING j.*)
   SELECT p."Id",left(p."Title",600),left(p."OriginalText",900),p."PublishedAt",p."SourceId",j.dirty_generation,j.lease_token
   FROM leased j JOIN "Publications" p ON p."Id"=j.publication_id;
   """,("r",Revision),("archive",archive),("token",Guid.NewGuid().ToString("N")));
  await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))list.Add(Read(reader));return list;
 }
 static SemanticArticle Read(NpgsqlDataReader r)=>new(r.GetInt32(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetDateTime(3),r.IsDBNull(4)?null:r.GetInt32(4),r.GetInt64(5),r.GetString(6));
 public async Task<bool> Complete(SemanticArticle article,float[] vector,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var gate=Cmd(db,tx,"SELECT pg_advisory_xact_lock(23011,12); SET LOCAL hnsw.ef_search=100; SET LOCAL hnsw.iterative_scan=strict_order; SET LOCAL hnsw.max_scan_tuples=20000; SET LOCAL statement_timeout='20s'"))await gate.ExecuteNonQueryAsync(ct);
  await using(var parent=Cmd(db,tx,"SELECT p.\"Id\" FROM \"Publications\" p WHERE p.\"Id\"=@id FOR SHARE",("id",article.Id)))await parent.ExecuteScalarAsync(ct);
  SemanticArticle current;
  await using(var check=Cmd(db,tx,"""
   SELECT p."Id",left(p."Title",600),left(p."OriginalText",900),p."PublishedAt",p."SourceId",j.dirty_generation,j.lease_token
   FROM news_local_semantic_jobs j JOIN "Publications" p ON p."Id"=j.publication_id LEFT JOIN "Sources" s ON s."Id"=p."SourceId"
   WHERE j.publication_id=@id AND j.model_revision=@r AND j.lease_token=@token AND j.lease_until>clock_timestamp() AND j.dirty_generation=@generation AND coalesce(s."CollectionKind",'')<>'Social' FOR UPDATE OF j
   """,("id",article.Id),("r",Revision),("token",article.Token),("generation",article.Generation)))
  {await using var rd=await check.ExecuteReaderAsync(ct);if(!await rd.ReadAsync(ct))return false;current=Read(rd);}
  if(current.Hash!=article.Hash)return false;
  await using(var save=Cmd(db,tx,"""
   INSERT INTO news_local_semantic_vectors(publication_id,model_revision,input_hash,embedding,title,body,published_at,source_id)
   VALUES(@id,@r,@hash,@vector::vector,@title,@body,@date,@source)
   ON CONFLICT(publication_id,model_revision) DO UPDATE SET input_hash=excluded.input_hash,embedding=excluded.embedding,title=excluded.title,body=excluded.body,published_at=excluded.published_at,source_id=excluded.source_id,embedded_at=now();
   UPDATE news_local_semantic_jobs SET state='Completed',observed_generation=dirty_generation,processed_at=now(),lease_token=NULL,lease_until=NULL,last_error=NULL,attempts=0 WHERE publication_id=@id AND model_revision=@r;
   DELETE FROM news_semantic_candidates WHERE model_id=@r AND (publication_id_a=@id OR publication_id_b=@id);
   UPDATE news_local_semantic_pairs SET state='Invalidated',lease_token=NULL,lease_until=NULL
    WHERE model_revision=@r AND ((a=@id AND hash_a<>@hash) OR (b=@id AND hash_b<>@hash));
   """,("id",article.Id),("r",Revision),("hash",article.Hash),("vector",LocalSemantic.Vector(vector)),("title",article.Title),("body",article.Body),("date",article.Published),("source",article.Source)))await save.ExecuteNonQueryAsync(ct);
  var matches=new List<(int Id,string Hash,float Score)>();
  await using(var near=Cmd(db,tx,"""
   WITH near AS MATERIALIZED(
    SELECT v.publication_id,v.input_hash,v.model_revision,v.embedding<=>@vector::vector AS distance
    FROM news_local_semantic_vectors v
    WHERE v.model_revision=@r AND v.publication_id<>@id AND v.published_at BETWEEN @from AND @to
    ORDER BY v.embedding<=>@vector::vector LIMIT 256
   )
   SELECT v.publication_id,v.input_hash,1-v.distance AS score
   FROM near v JOIN news_local_semantic_jobs j ON j.publication_id=v.publication_id AND j.model_revision=v.model_revision
   JOIN "Publications" p ON p."Id"=v.publication_id LEFT JOIN "Sources" s ON s."Id"=p."SourceId"
   WHERE j.state='Completed' AND j.dirty_generation=j.observed_generation AND coalesce(s."CollectionKind",'')<>'Social'
   ORDER BY v.distance LIMIT 64;
   """,("r",Revision),("id",article.Id),("vector",LocalSemantic.Vector(vector)),("from",article.Published.AddHours(-48)),("to",article.Published.AddHours(48))))
  {await using var rd=await near.ExecuteReaderAsync(ct);while(await rd.ReadAsync(ct)){var score=(float)Math.Clamp(rd.GetDouble(2),-1,1);if(score>=0.72)matches.Add((rd.GetInt32(0),rd.GetString(1),score));}}
  var index=0;
  foreach(var match in matches.OrderByDescending(x=>x.Score).Take(8))
  {
   var a=Math.Min(article.Id,match.Id);var b=Math.Max(article.Id,match.Id);var ha=a==article.Id?article.Hash:match.Hash;var hb=b==article.Id?article.Hash:match.Hash;
   await using var pair=Cmd(db,tx,"""
    INSERT INTO news_local_semantic_pairs AS p(a,b,model_revision,hash_a,hash_b,similarity,state) VALUES(@a,@b,@r,@ha,@hb,@score,@state)
    ON CONFLICT(a,b,model_revision) DO UPDATE SET hash_a=excluded.hash_a,hash_b=excluded.hash_b,similarity=excluded.similarity,
     state=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN CASE WHEN p.state='RelatedOnly' AND excluded.state='Pending' THEN 'Pending' ELSE p.state END ELSE excluded.state END,
     attempts=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.attempts ELSE 0 END,
     decision=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.decision END,
     confidence=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.confidence END,
     reason=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.reason END,
     judge_revision=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.judge_revision END,
     judged_at=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.judged_at END,
     lease_token=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.lease_token END,
     lease_until=CASE WHEN p.hash_a=excluded.hash_a AND p.hash_b=excluded.hash_b AND p.state<>'Invalidated' THEN p.lease_until END,next_at=now();
    INSERT INTO news_semantic_candidates(publication_id_a,publication_id_b,model_id,cosine_similarity) VALUES(@a,@b,@r,@score) ON CONFLICT(publication_id_a,publication_id_b,model_id) DO UPDATE SET cosine_similarity=excluded.cosine_similarity;
    """,("a",a),("b",b),("r",Revision),("ha",ha),("hb",hb),("score",match.Score),("state",index++<3&&match.Score>=0.80?"Pending":"RelatedOnly"));await pair.ExecuteNonQueryAsync(ct);
  }
  await tx.CommitAsync(ct);return true;
 }
 public async Task Fail(SemanticArticle article,string? error,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,null,"""
   UPDATE news_local_semantic_jobs SET lease_token=NULL,lease_until=NULL,state=CASE WHEN @error='' THEN 'Pending' ELSE 'Failed' END,
    attempts=attempts+CASE WHEN @error='' THEN 0 ELSE 1 END,last_error=nullif(@error,''),next_at=now()+CASE WHEN @error='' THEN interval '5 seconds' ELSE interval '5 minutes' END
   WHERE publication_id=@id AND model_revision=@r AND lease_token=@token;
   """,("error",LocalCountryClassifier.Limit(error??"",300)),("id",article.Id),("r",Revision),("token",article.Token));await cmd.ExecuteNonQueryAsync(ct);
 }
}
