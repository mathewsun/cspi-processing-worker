namespace Cspi.ProcessingWorker;
public sealed class LocalSemanticService(IConfiguration config,IHttpClientFactory factory,ILogger<LocalSemanticService> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  if(!config.GetValue("LocalSemantic:Enabled",false))return;
  var store=new LocalSemanticStore(config.GetConnectionString("DefaultConnection")!);var client=factory.CreateClient("local-semantic");var turn=0;var initialized=false;
  while(!ct.IsCancellationRequested)
  {
   var batch=new List<SemanticArticle>();
   try
   {
    await LocalSemantic.Verify(client,ct);if(!initialized){await store.Initialize(ct);initialized=true;}
    await store.Enqueue(ct);batch=await store.Claim(turn++%5==4,ct);
    if(batch.Count==0){await Task.Delay(2000,ct);continue;}
    var vectors=await LocalSemantic.Infer(client,batch,ct);var done=0;
    for(var i=0;i<batch.Count;i++){if(await store.Complete(batch[i],vectors[i],ct))done++;else await store.Fail(batch[i],null,ct);}
    logger.LogInformation("Local semantic embedded {Done}/{Count} news items",done,batch.Count);await Task.Delay(250,ct);
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch(Exception ex){logger.LogWarning(ex,"Local semantic batch failed");foreach(var article in batch)await store.Fail(article,ex.Message,ct);await Task.Delay(10000,ct);}
  }
 }
}
