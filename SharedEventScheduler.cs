namespace Cspi.ProcessingWorker;
// Return true only when the shared Qwen turn was consumed. Cheap social searches
// must fall through to discussion/country work even when social backlog remains.
public sealed class SharedEventScheduler(LocalEventService? events,SocialSemanticService? social)
{
 int turn;
 public async Task<bool> TryRun(HttpClient client,CancellationToken ct)
 {
  async Task<bool> Social()
  {
   if(social is null||!await social.TryRun(client,ct))return false;
   turn++;return social.UsedInference;
  }
  async Task<bool> News()
  {
   if(events is null||!await events.TryRun(client,ct))return false;
   turn++;return true;
  }
  return turn%2==0?await Social()||await News():await News()||await Social();
 }
}
