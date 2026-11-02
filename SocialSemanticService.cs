namespace Cspi.ProcessingWorker;
public sealed class SocialSemanticService(SocialSemanticStore store,LocalEventStore budget,string model,HttpClient embedding,ILogger logger)
{
 int turn;
 public async Task<bool> TryRun(HttpClient qwen,CancellationToken ct)
 {
  var post=await store.Claim(turn++%5==4,ct);if(post is null)return false;var timer=System.Diagnostics.Stopwatch.StartNew();
  try
  {
   var input=new SemanticArticle(0,LocalCountryClassifier.Limit(post.Body,600),null,post.Published,null,0,"");
   var vectors=await LocalSemantic.Infer(embedding,[input],ct);
   SocialSemanticMatch? match=null;EventJudgment? judgment=null;var result="UnsupportedLanguage";
   if(SemanticAutoEvents.SupportedLanguages.Contains(input.Language!)&&input.LanguageConfidence>=0.75)
   {
    match=await store.Nearest(post,vectors[0],ct);result="NoUnreviewedMatch";
    if(match is not null)
    {
     var a=match.Article;
     judgment=await LocalEventJudge.Infer(qwen,model,new(a.Id,0,a.Hash,post.Hash,a.Title,a.Body,a.Published,input.Title,LocalCountryClassifier.Limit(post.Body,1400),post.Published,post.Token),ct);
     result=judgment.Decision;
    }
   }
   var applied=await store.Complete(post,match,judgment,result,ct);if(!applied)await store.Release(post,ct);
   logger.LogInformation("Social semantic post {Post}: {Result}, article={Article}, applied={Applied}",post.Id,result,match?.Article.Id,applied);
  }
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception ex){logger.LogWarning(ex,"Social semantic post {Post} failed",post.Id);await store.Release(post,ct);}
  finally{if(!ct.IsCancellationRequested)await budget.Cooldown(timer.Elapsed.TotalSeconds*9,ct);}
  return true;
 }
}
