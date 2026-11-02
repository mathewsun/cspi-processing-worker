using System.Text.Json;
using Npgsql;

namespace Cspi.ProcessingWorker;

public sealed record LocalCountrySettings(bool Enabled,string Model,string Revision,string Digest)
{
    public static LocalCountrySettings Read(IConfiguration config) => new(
        config.GetValue("LocalCountry:Enabled",false),config["LocalCountry:Model"]??"spectrum-country:v1",
        config["LocalCountry:Revision"]??"qwen3-359d7dd4-country-name-v1",
        config["LocalCountry:Digest"]??"359d7dd4bcdab3d86b87d73ac27966f4dbb9f5efdfcc75d34a8764a09474fae7");
}

public sealed class LocalCountryQueueService(LocalCountrySettings settings,LocalCountryStore store,ILogger<LocalCountryQueueService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!settings.Enabled)return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await store.Initialize(ct);var count=await store.Enqueue(ct);
                if(count>0)logger.LogInformation("Local country queue: added {Count} articles",count);
                await Task.Delay(TimeSpan.FromSeconds(count>=10000?1:15),ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Local country queue error");await Task.Delay(TimeSpan.FromSeconds(30),ct);}
        }
    }
}

public sealed class LocalCountryService(LocalCountrySettings settings,LocalCountryStore store,IHttpClientFactory factory,
    IConfiguration config,ILogger<LocalCountryService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!settings.Enabled)return;
        await store.Initialize(ct);
        // One local inference stream across revisions/instances; connection retains the session lock.
        await using var singleton=new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
        await singleton.OpenAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_try_advisory_lock(23011,10)",singleton))
            if(await gate.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("Another local country classifier is active");
        var client=factory.CreateClient("local-country");var turn=0;var catalog=await store.Countries(ct);var refreshed=DateTimeOffset.UtcNow;
        while(!ct.IsCancellationRequested)
        {
            AiArticle? article=null;
            try
            {
                await using(var alive=new NpgsqlCommand("SELECT 1",singleton))await alive.ExecuteScalarAsync(ct);
                await VerifyModel(client,ct);
                if(DateTimeOffset.UtcNow-refreshed>TimeSpan.FromMinutes(5)){catalog=await store.Countries(ct);refreshed=DateTimeOffset.UtcNow;}
                article=await store.Claim(turn++%5==4,ct); // Four live slots, then one guaranteed archive slot; fallback if either is empty.
                if(article is null){await Task.Delay(TimeSpan.FromSeconds(5),ct);continue;}
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var relations=await LocalCountryClassifier.Infer(client,settings.Model,catalog,article,ct);
                var applied=await store.Complete(article,relations,ct);
                logger.LogInformation("Local country {Publication}: {Count} relations, applied={Applied}, scope={Scope}, {Seconds:F1}s",
                    article.Id,relations.Count,applied,article.Scope,timer.Elapsed.TotalSeconds);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                if(singleton.State!=System.Data.ConnectionState.Open)throw;
                logger.LogWarning(ex,"Local country classification failed for {Publication}",article?.Id);
                if(article is not null)await store.Fail(article,ex.Message,ct);
                await Task.Delay(TimeSpan.FromSeconds(article is null?30:2),ct);
            }
        }
    }
    private async Task VerifyModel(HttpClient client,CancellationToken ct)
    {
        using var response=await client.GetAsync("api/tags",ct);response.EnsureSuccessStatusCode();
        using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!data.RootElement.GetProperty("models").EnumerateArray().Any(m=>m.GetProperty("name").GetString()==settings.Model &&
            m.GetProperty("digest").GetString()==settings.Digest))throw new InvalidOperationException("Pinned local model missing or digest changed");
    }
}
