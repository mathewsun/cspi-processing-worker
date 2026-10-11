using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Opt-in bounded incremental enqueue. Does not bypass model validation or accept proposals.
/// </summary>
public sealed class CountryLlmQueueService(
    IConfiguration configuration, ILogger<CountryLlmQueueService> logger) : BackgroundService
{
    private readonly string connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database connection");
    private readonly string model = configuration["CountryLlm:Model"] ?? "";
    private readonly int batch = Math.Clamp(configuration.GetValue("CountryLlm:EnqueueBatch", 100), 1, 1000);
    private readonly int maxPending = Math.Clamp(configuration.GetValue("CountryLlm:MaxPending", 1000), 100, 100000);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("CountryLlm:AutoEnqueue", false)) return;
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("CountryLlm:Model is required");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var count = await Enqueue(ct);
                if (count>0) logger.LogInformation("Enqueued {Count} country LLM jobs",count);
                await Task.Delay(TimeSpan.FromSeconds(30),ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex,"Country LLM queue synchronization failed");
                await Task.Delay(TimeSpan.FromSeconds(30),ct);
            }
        }
    }

    private async Task<int> Enqueue(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH capacity AS (
                SELECT GREATEST(0,@maxPending-count(*))::integer AS slots
                FROM news_country_llm_jobs
                WHERE model_id=@model AND state<>'Completed' AND attempts<3
            ), due AS (
                SELECT p."Id"
                FROM "Publications" p
                JOIN news_processing_jobs j ON j.publication_id=p."Id"
                WHERE j.processed_at IS NOT NULL
                  AND length(COALESCE(p."OriginalText",''))>=100
                  AND NOT EXISTS (
                    SELECT 1 FROM news_country_llm_jobs q
                    WHERE q.publication_id=p."Id" AND q.model_id=@model)
                ORDER BY p."Id" DESC
                LIMIT (SELECT LEAST(@batch,slots) FROM capacity)
            )
            INSERT INTO news_country_llm_jobs(publication_id,model_id)
            SELECT "Id",@model FROM due
            ON CONFLICT DO NOTHING;
            """,db);
        cmd.Parameters.AddWithValue("model",model);
        cmd.Parameters.AddWithValue("batch",batch);
        cmd.Parameters.AddWithValue("maxPending",maxPending);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
