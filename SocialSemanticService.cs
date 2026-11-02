namespace Cspi.ProcessingWorker;
public sealed class SocialSemanticService(SocialSemanticStore store,LocalEventStore budget,string model,HttpClient embedding,ILogger logger,int maxSearches=16)
{
 int turn;
 DateTimeOffset searchRetryAt;
 public async Task<bool> TryRun(HttpClient qwen,CancellationToken ct)
 {
  if(DateTimeOffset.UtcNow<searchRetryAt)return false;
  var processed=false;
  // Bounded cheap searches may share a scheduler turn. One Qwen call ends the turn
  // and reserves the same persisted budget used by news-event judgments.
  for(var scan=0;scan<Math.Clamp(maxSearches,1,16);scan++)
  {
  var post=await store.Claim(turn%5==4,ct);if(post is null)return processed;turn++;processed=true;
  System.Diagnostics.Stopwatch? judgeTimer=null;
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
     judgeTimer=System.Diagnostics.Stopwatch.StartNew();
     judgment=await LocalEventJudge.Infer(qwen,model,new(a.Id,0,a.Hash,post.Hash,a.Title,a.Body,a.Published,input.Title,LocalCountryClassifier.Limit(post.Body,1400),post.Published,post.Token),ct);
     judgeTimer.Stop();
     result=judgment.Decision;
    }
   }
   var applied=await store.Complete(post,match,judgment,result,ct);if(!applied)await store.Release(post,ct);
   logger.LogInformation("Social semantic post {Post}: {Result}, article={Article}, applied={Applied}",post.Id,result,match?.Article.Id,applied);
  }
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception ex){logger.LogWarning(ex,"Social semantic post {Post} failed",post.Id);if(judgeTimer is null)searchRetryAt=DateTimeOffset.UtcNow.AddSeconds(10);await store.Release(post,ct);return true;}
  finally{if(judgeTimer is not null&&!ct.IsCancellationRequested)await budget.Cooldown(judgeTimer.Elapsed.TotalSeconds*9,ct);}
  if(judgeTimer is not null)return true;
  }
  return processed;
 }
}
