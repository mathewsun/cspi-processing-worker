using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Conservative non-blocking automation: only pairs supported by two independent
/// similarity signals are approved. Human rejections and split blocks always win.
/// Disabled by default until precision is calibrated on labeled articles.
/// </summary>
public sealed class AutoEventDecisionService(
    IConfiguration config, ILogger<AutoEventDecisionService> logger) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database connection");
    private readonly float semanticMin = Math.Clamp(config.GetValue("AutoEvents:SemanticMin", 0.96f), 0.9f, 0.999f);
    private readonly float titleMin = Math.Clamp(config.GetValue("AutoEvents:TitleMin", 0.90f), 0.8f, 1f);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("AutoEvents:Enabled", false))
        {
            logger.LogInformation("Automatic event decisions disabled");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var count = await ApproveBatch(ct);
                if (count > 0) logger.LogInformation("Automatically approved {Count} high-evidence pairs", count);
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic event decision failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<int> ApproveBatch(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH eligible AS (
                SELECT s.publication_id_a AS a, s.publication_id_b AS b,
                       MAX(s.cosine_similarity) AS semantic_score,
                       MAX(t.similarity) AS title_score
                FROM news_semantic_candidates s
                JOIN news_near_duplicate_candidates t
                  ON t.publication_id_a=s.publication_id_a
                 AND t.publication_id_b=s.publication_id_b
                JOIN "Publications" pa ON pa."Id"=s.publication_id_a
                JOIN "Publications" pb ON pb."Id"=s.publication_id_b
                WHERE s.cosine_similarity >= @semantic
                  AND t.similarity >= @title
                  AND pa."SourceId" IS DISTINCT FROM pb."SourceId"
                  AND ABS(EXTRACT(EPOCH FROM pa."PublishedAt"-pb."PublishedAt")) <= 43200
                  AND NOT EXISTS (
                      SELECT 1 FROM news_event_pair_blocks x
                      WHERE x.publication_id_a=s.publication_id_a
                        AND x.publication_id_b=s.publication_id_b)
                  AND NOT EXISTS (
                      SELECT 1 FROM news_event_link_reviews r
                      WHERE r.publication_id_a=s.publication_id_a
                        AND r.publication_id_b=s.publication_id_b)
                GROUP BY s.publication_id_a,s.publication_id_b
                ORDER BY MAX(s.cosine_similarity) DESC LIMIT 100
            )
            INSERT INTO news_event_link_reviews
                (publication_id_a,publication_id_b,score,evidence,decision,reviewed_at)
            SELECT a,b,LEAST(semantic_score,title_score),
                   jsonb_build_object('method','AutoDualEvidenceV1',
                     'semantic',semantic_score,'title',title_score),
                   'Approved',now()
            FROM eligible
            ON CONFLICT DO NOTHING;
            """, db);
        command.Parameters.AddWithValue("semantic", semanticMin);
        command.Parameters.AddWithValue("title", titleMin);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
