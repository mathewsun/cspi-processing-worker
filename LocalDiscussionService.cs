using Microsoft.Extensions.Logging;
namespace Cspi.ProcessingWorker;
public sealed class LocalDiscussionService(LocalDiscussionStore store,string model,ILogger logger)
{
 public Task Initialize(CancellationToken ct)=>store.Initialize(ct);
 public async Task<bool> TryRun(HttpClient client,CancellationToken ct)
 {
  var work=await store.Claim(ct);if(work is null)return false;
  var timer=System.Diagnostics.Stopwatch.StartNew();var inferred=false;
  try
  {
   var sample=await store.Sample(work.Post,ct);
   if(sample is null){await store.Release(work,null,ct);return false;}
   var previous=await store.Prepare(work,sample,ct);
   if(previous is null){await store.Release(work,null,ct);return false;}
   var batch=sample.Comments.Skip(previous.Count).Take(4).ToArray();
   var added=new List<DiscussionLabel>();
   if(batch.Length>0){inferred=true;added=await LocalDiscussionClassifier.Infer(client,model,sample,batch,ct);}
   var applied=await store.Complete(work,sample,previous,added,ct);
   if(!applied)await store.Release(work,null,ct);
   logger.LogInformation("Local discussion post {Post}: {Analyzed}/{Sample}, applied={Applied}, inference={Inference}, {Seconds:F1}s",work.Post,previous.Count+added.Count,sample.Comments.Count,applied,inferred,timer.Elapsed.TotalSeconds);
  }
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception ex){logger.LogWarning(ex,"Local discussion failed for {Post}",work.Post);await store.Release(work,ex.Message,ct);}
  finally
  {
   // Reserve approximately 10% of wall time for discussion inference, across restarts.
   // Schema checks / unchanged samples need no long cooldown and no model call.
   if(!ct.IsCancellationRequested)await store.Cooldown(inferred?timer.Elapsed.TotalSeconds*9:10,ct);
  }
  return inferred;
 }
}
