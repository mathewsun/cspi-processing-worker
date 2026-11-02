namespace Cspi.ProcessingWorker;
public sealed class LocalEventService(LocalEventStore store,string model,ILogger logger)
{
 int turn;
 public async Task<bool> TryRun(HttpClient client,CancellationToken ct)
 {
  var pair=await store.Claim(ct,turn%5==4);if(pair is null)return false;turn++;var timer=System.Diagnostics.Stopwatch.StartNew();
  try{var judgment=await LocalEventJudge.Infer(client,model,pair,ct);var applied=await store.Complete(pair,judgment,ct);if(!applied)await store.Release(pair,null,ct);logger.LogInformation("Local event {A}/{B}: {Decision} confidence={Confidence}, applied={Applied}, {Seconds:F1}s",pair.A,pair.B,judgment.Decision,judgment.Confidence,applied,timer.Elapsed.TotalSeconds);}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception ex){logger.LogWarning(ex,"Local event judgment failed for {A}/{B}",pair.A,pair.B);await store.Release(pair,ex.Message,ct);}
  finally{if(!ct.IsCancellationRequested)await store.Cooldown(timer.Elapsed.TotalSeconds*9,ct);}
  return true;
 }
}
