using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Generates candidate links between articles for later event clustering.
/// This service NEVER creates Events or writes final country assignments.
/// Disabled by default until candidate quality is evaluated.
/// </summary>
public sealed class EventReviewCandidateService(
    IConfiguration configuration,
    ILogger<EventReviewCandidateService> logger) : BackgroundService
{
    private readonly string connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Database connection is required");
    private readonly int batch = Math.Clamp(configuration.GetValue("EventReview:BatchSize", 500), 1, 2000);
    private readonly double semanticThreshold = Math.Clamp(
        configuration.GetValue("EventReview:SemanticThreshold", 0.93), 0.8, 0.999);
    private readonly double titleThreshold = Math.Clamp(
        configuration.GetValue("EventReview:TitleThreshold", 0.9), 0.8, 1.0);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("EventReview:Enabled", false))
        {
            logger.LogInformation("Event candidate review generation disabled");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var inserted = await GenerateAsync(ct);
                if (inserted > 0)
                    logger.LogInformation("Generated {Count} new event-link review candidates", inserted);
                await Task.Delay(TimeSpan.FromMinutes(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event candidate generation failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<int> GenerateAsync(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH evidence AS (
                SELECT publication_id_a AS a, publication_id_b AS b,
                       MAX(cosine_similarity) AS semantic_score,
                       NULL::real AS title_score
                FROM news_semantic_candidates
                WHERE cosine_similarity >= @semantic
                GROUP BY publication_id_a, publication_id_b
                UNION ALL
                SELECT publication_id_a, publication_id_b,
                       NULL::real, MAX(similarity)
                FROM news_near_duplicate_candidates
                WHERE similarity >= @title
                GROUP BY publication_id_a, publication_id_b
            ), grouped AS (
                SELECT a, b, MAX(semantic_score) AS semantic_score,
                       MAX(title_score) AS title_score
                FROM evidence GROUP BY a,b
            ), eligible AS (
                SELECT g.*, pa."SourceId" AS source_a, pb."SourceId" AS source_b,
                       pa."PublishedAt" AS published_a, pb."PublishedAt" AS published_b
                FROM grouped g
                JOIN "Publications" pa ON pa."Id"=g.a
                JOIN "Publications" pb ON pb."Id"=g.b
                WHERE pa."SourceId" IS DISTINCT FROM pb."SourceId"
                  AND ABS(EXTRACT(EPOCH FROM (pa."PublishedAt"-pb."PublishedAt"))) <= 43200
                  AND (g.semantic_score >= @semantic OR g.title_score >= @title)
                  AND NOT EXISTS (
                      SELECT 1 FROM news_event_link_reviews r
                      WHERE r.publication_id_a=g.a AND r.publication_id_b=g.b
                  )
                ORDER BY GREATEST(COALESCE(g.semantic_score,0),COALESCE(g.title_score,0)) DESC
                LIMIT @limit
            )
            INSERT INTO news_event_link_reviews
                (publication_id_a,publication_id_b,score,evidence)
            SELECT a,b,
                   GREATEST(COALESCE(semantic_score,0),COALESCE(title_score,0)),
                   jsonb_build_object(
                     'semantic',semantic_score,'title',title_score,
                     'source_a',source_a,'source_b',source_b,
                     'published_a',published_a,'published_b',published_b,
                     'method','CandidateV1')
            FROM eligible
            ON CONFLICT DO NOTHING;
            """, db);
        cmd.Parameters.AddWithValue("semantic", semanticThreshold);
        cmd.Parameters.AddWithValue("title", titleThreshold);
        cmd.Parameters.AddWithValue("limit", batch);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
