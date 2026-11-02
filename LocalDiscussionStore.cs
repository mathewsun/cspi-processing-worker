using Npgsql;
using System.Text.Json;
namespace Cspi.ProcessingWorker;
public sealed record DiscussionWork(long Post,string Token);
public sealed class LocalDiscussionStore(string connection,string model,string digest)
{
 const string Revision=LocalDiscussionClassifier.Revision;
 static NpgsqlCommand Cmd(NpgsqlConnection db,string sql,NpgsqlTransaction? tx=null,params(string,object)[] args)
 {var c=new NpgsqlCommand(sql,db,tx);foreach(var(k,v)in args)c.Parameters.AddWithValue(k,v);return c;}
 async Task<NpgsqlConnection> Open(CancellationToken ct){var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);return db;}
 public async Task Initialize(CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var cmd=Cmd(db,"""
   INSERT INTO social_local_discussion_control(id,enabled,model_revision,model_name,model_digest)
   VALUES(true,true,@rev,@model,@digest) ON CONFLICT(id) DO UPDATE SET enabled=true,model_revision=@rev,model_name=@model,model_digest=@digest;
   UPDATE social_jobs SET next_at='infinity',lease_owner=NULL,lease_until=NULL,generation=generation+1,access_state='LocalAnalysis'
   WHERE kind='Discussion' AND (next_at<>'infinity'::timestamptz OR lease_owner IS NOT NULL);
   INSERT INTO social_local_discussion_jobs(post_id,model_revision)
   SELECT p.id,@rev FROM social_posts p WHERE NOT p.deleted AND EXISTS(SELECT 1 FROM social_comments c WHERE c.post_id=p.id AND NOT c.deleted AND btrim(c.body)<>'')
   ON CONFLICT DO NOTHING;
   """,tx,("rev",Revision),("model",model),("digest",digest));
  await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
 }
 public async Task<DiscussionWork?> Claim(CancellationToken ct)
 {
  await using var db=await Open(ct);var token=Guid.NewGuid().ToString("N");
  await using var cmd=Cmd(db,"""
   WITH due AS (
    SELECT j.post_id FROM social_local_discussion_jobs j JOIN social_posts p ON p.id=j.post_id JOIN social_channels c ON c.id=p.channel_id JOIN "Sources" s ON s."Id"=c.source_id
    WHERE j.model_revision=@rev AND j.next_at<=now() AND j.attempts<3 AND (j.lease_until IS NULL OR j.lease_until<now())
     AND NOT p.deleted AND c.enabled AND c.canonical_id IS NULL AND s."CollectionKind"='Social' AND s."CollectionEnabled" IS TRUE AND s."Status" NOT IN(4,5)
     AND EXISTS(SELECT 1 FROM social_local_discussion_control WHERE enabled AND model_revision=@rev AND next_slot_at<=now())
    ORDER BY (j.state='Ready'),j.next_at,j.post_id DESC FOR UPDATE OF j SKIP LOCKED LIMIT 1
   ) UPDATE social_local_discussion_jobs j SET lease_token=@token,lease_until=now()+interval '5 minutes'
    FROM due WHERE j.post_id=due.post_id AND j.model_revision=@rev RETURNING j.post_id;
   """,null,("rev",Revision),("token",token));
  var id=await cmd.ExecuteScalarAsync(ct);return id is null?null:new(Convert.ToInt64(id),token);
 }
 public async Task<DiscussionSample?> Sample(long post,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
  string target,hash;long generation;
  await using(var cmd=Cmd(db,"SELECT left(p.body,800),p.body_hash,j.dirty_generation FROM social_posts p JOIN social_local_discussion_jobs j ON j.post_id=p.id WHERE p.id=@p AND j.model_revision=@rev AND NOT p.deleted",tx,("p",post),("rev",Revision)))
  {await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;target=LocalCountryClassifier.Limit(r.GetString(0),800);hash=r.GetString(1);generation=r.GetInt64(2);}
  var texts=new List<DiscussionText>();
  await using(var cmd=Cmd(db,"SELECT id,left(body,400),body_hash FROM social_comments WHERE post_id=@p AND NOT deleted AND btrim(body)<>'' ORDER BY id LIMIT 100",tx,("p",post)))
  {await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))texts.Add(new(r.GetInt64(0),LocalCountryClassifier.Limit(r.GetString(1),400),r.GetString(2)));}
  await using var count=Cmd(db,"SELECT count(*) FROM social_comments WHERE post_id=@p AND NOT deleted AND btrim(body)<>''",tx,("p",post));
  var total=Convert.ToInt64(await count.ExecuteScalarAsync(ct));await tx.CommitAsync(ct);return new(target,hash,total,texts,generation);
 }
 public async Task<List<DiscussionLabel>?> Prepare(DiscussionWork work,DiscussionSample sample,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var cmd=Cmd(db,"""
   UPDATE social_local_discussion_jobs SET
     labels_json=CASE WHEN input_hash=@hash THEN labels_json ELSE '[]'::jsonb END,
     analyzed_count=CASE WHEN input_hash=@hash THEN analyzed_count ELSE 0 END,
     input_hash=@hash,sample_json=@sample::jsonb,sample_count=@count,state='Partial',updated_at=now()
   WHERE post_id=@p AND model_revision=@rev AND lease_token=@token AND lease_until>clock_timestamp() AND dirty_generation=@generation
   RETURNING labels_json::text;
   """,tx,("hash",sample.Hash),("sample",JsonSerializer.Serialize(sample)),("count",sample.Comments.Count),("p",work.Post),("rev",Revision),("token",work.Token),("generation",sample.Generation));
  var json=await cmd.ExecuteScalarAsync(ct);if(json is null)return null;
  var labels=JsonSerializer.Deserialize<List<DiscussionLabel>>((string)json)!;
  if(labels.Count>sample.Comments.Count||!labels.Select(x=>x.Comment).SequenceEqual(sample.Comments.Take(labels.Count).Select(x=>x.Id)))throw new InvalidDataException("Stored analysis checkpoint inconsistent");
  await tx.CommitAsync(ct);return labels;
 }
 public async Task<bool> Complete(DiscussionWork work,DiscussionSample sample,IReadOnlyList<DiscussionLabel> previous,IReadOnlyList<DiscussionLabel> added,CancellationToken ct)
 {
  var current=await Sample(work.Post,ct);if(current is null||current.Hash!=sample.Hash)return false;
  var labels=previous.Concat(added).ToList();if(labels.Count>sample.Comments.Count||!labels.Select(x=>x.Comment).SequenceEqual(sample.Comments.Take(labels.Count).Select(x=>x.Id)))throw new InvalidDataException("Invalid analysis prefix");
  await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
  // Source/channel pause serializes with commit; use the source-then-channel
  // order of the application controls before locking the analysis checkpoint.
  await using(var source=Cmd(db,"SELECT s.\"Id\" FROM \"Sources\" s JOIN social_channels c ON c.source_id=s.\"Id\" JOIN social_posts p ON p.channel_id=c.id WHERE p.id=@p FOR SHARE OF s",tx,("p",work.Post)))await source.ExecuteScalarAsync(ct);
  await using(var channel=Cmd(db,"SELECT c.id FROM social_channels c JOIN social_posts p ON p.channel_id=c.id WHERE p.id=@p FOR SHARE OF c",tx,("p",work.Post)))await channel.ExecuteScalarAsync(ct);
  await using(var fence=Cmd(db,"""
   SELECT 1 FROM social_local_discussion_jobs j JOIN social_posts p ON p.id=j.post_id JOIN social_channels c ON c.id=p.channel_id JOIN "Sources" s ON s."Id"=c.source_id
   WHERE j.post_id=@p AND j.model_revision=@rev AND j.lease_token=@token AND j.lease_until>clock_timestamp() AND j.dirty_generation=@generation
    AND c.enabled AND c.canonical_id IS NULL AND NOT p.deleted AND s."CollectionKind"='Social' AND s."CollectionEnabled" IS TRUE AND s."Status" NOT IN(4,5) FOR UPDATE OF j
   """,tx,("p",work.Post),("rev",Revision),("token",work.Token),("generation",current.Generation)))if(await fence.ExecuteScalarAsync(ct) is null)return false;
  var finished=labels.Count==sample.Comments.Count;
  await using(var update=Cmd(db,"""
   UPDATE social_local_discussion_jobs SET labels_json=@labels::jsonb,analyzed_count=@count,state=@state,
    next_at=now()+@seconds*interval '1 second',lease_token=NULL,lease_until=NULL,attempts=0,last_error=NULL,
    observed_generation=@generation,updated_at=now() WHERE post_id=@p AND model_revision=@rev;
   """,tx,("labels",JsonSerializer.Serialize(labels)),("count",labels.Count),("state",sample.Comments.Count==0?"NoComments":finished?"Ready":"Partial"),("seconds",finished?3600:0),("generation",current.Generation),("p",work.Post),("rev",Revision)))await update.ExecuteNonQueryAsync(ct);
  if(added.Count>0)
  {
   await using var save=Cmd(db,"""
    INSERT INTO social_local_discussion_batches(post_id,model_revision,input_hash,first_index,sample_json,labels_json)
    VALUES(@p,@rev,@hash,@first,@sample::jsonb,@added::jsonb) ON CONFLICT DO NOTHING;
    INSERT INTO social_discussion_aggregates(post_id,run_id,state,schema_version,model,model_version,confidence,sample_count,analyzed_count,available_count,coverage,payload,input_hash)
    VALUES(@p,@run,'Ready',@schema,@model,@rev,@confidence,@sampleCount,@analyzed,@available,@coverage,@payload::jsonb,@hash);
    """,tx,("p",work.Post),("rev",Revision),("hash",sample.Hash),("first",previous.Count),("sample",JsonSerializer.Serialize(sample)),("added",JsonSerializer.Serialize(added)),("run",Guid.NewGuid()),
    ("schema",LocalDiscussionClassifier.Schema),("model",model),("confidence",labels.Average(x=>x.Confidence)),("sampleCount",sample.Comments.Count),("analyzed",labels.Count),("available",current.Available),
    ("coverage",finished?"LocalFirst100Text400;CompleteSample":"LocalFirst100Text400;Partial"),("payload",JsonSerializer.Serialize(LocalDiscussionClassifier.Aggregate(sample,labels))));
   await save.ExecuteNonQueryAsync(ct);
  }
  await tx.CommitAsync(ct);return true;
 }
 public async Task Release(DiscussionWork work,string? error,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,"""
   UPDATE social_local_discussion_jobs SET lease_token=NULL,lease_until=NULL,
    state=CASE WHEN @error='' THEN 'Pending' ELSE 'Failed' END,attempts=attempts+CASE WHEN @error='' THEN 0 ELSE 1 END,
    last_error=nullif(@error,''),next_at=now()+CASE WHEN @error='' THEN interval '10 seconds' ELSE interval '15 minutes' END
   WHERE post_id=@p AND model_revision=@rev AND lease_token=@token;
   """,null,("error",LocalCountryClassifier.Limit(error??"",400)),("p",work.Post),("rev",Revision),("token",work.Token));await cmd.ExecuteNonQueryAsync(ct);
 }
 public async Task Cooldown(double seconds,CancellationToken ct)
 {
  await using var db=await Open(ct);await using var cmd=Cmd(db,"UPDATE social_local_discussion_control SET next_slot_at=now()+@seconds*interval '1 second' WHERE id AND model_revision=@rev",null,("seconds",Math.Clamp(seconds,10,3600)),("rev",Revision));await cmd.ExecuteNonQueryAsync(ct);
 }
}
