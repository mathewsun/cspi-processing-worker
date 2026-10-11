using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Moves already-collected RSS items into CSPI Publications and enqueues exact dedupe.
/// Every item, publication, mapping and job are committed atomically.
/// Multiple replicas can promote different staging rows using SKIP LOCKED.
/// </summary>
public sealed class StagingPromotionService(
    IConfiguration configuration,
    ILogger<StagingPromotionService> logger) : BackgroundService
{
    private readonly string connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");
    private readonly int batchSize = Math.Clamp(configuration.GetValue("Processor:PromotionBatchSize", 100), 1, 500);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Staging promotion service started, batch size {BatchSize}", batchSize);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var promoted = await PromoteBatch(ct);
                if (promoted == 0) await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed promoting collected news items");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task<int> PromoteBatch(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Lock staging rows until the publication and queue item have been created.
        // A shared source may cover multiple countries: this preserves its source
        // country for compatibility, while geographic classification happens later.
        await using var claim = new NpgsqlCommand("""
            SELECT i.id, i.source_id, i.item_url, i.title, i.published_at,
                   COALESCE(NULLIF(s."Country", ''), 'Unknown') AS source_country,
                   COALESCE(NULLIF(s."Language", ''), 'und') AS source_language
            FROM news_collected_items i
            JOIN "Sources" s ON s."Id" = i.source_id
            WHERE NOT EXISTS (
                SELECT 1 FROM news_staging_publications m
                WHERE m.staging_item_id = i.id
            )
            ORDER BY i.id
            LIMIT @limit
            FOR UPDATE OF i SKIP LOCKED;
            """, db, tx);
        claim.Parameters.AddWithValue("limit", batchSize);
        var batch = new List<(long Id, int SourceId, string Url, string Title, DateTime? PublishedAt, string Country, string Language)>();
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                batch.Add((
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    reader.GetString(5), reader.GetString(6)));
            }
        }

        foreach (var item in batch)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO "Publications"
                    ("Title", "Url", "Language", "Country", "PublishedAt",
                     "SourceId", "ProcessingState")
                VALUES (@title, @url, @language, @country, @published, @source, 'Собрана')
                RETURNING "Id";
                """, db, tx);
            insert.Parameters.AddWithValue("title", item.Title);
            insert.Parameters.AddWithValue("url", item.Url);
            insert.Parameters.AddWithValue("language", item.Language);
            insert.Parameters.AddWithValue("country", item.Country);
            insert.Parameters.AddWithValue("published", item.PublishedAt ?? DateTime.UtcNow);
            insert.Parameters.AddWithValue("source", item.SourceId);
            var publicationId = (int)(await insert.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Publication insertion returned no ID"));

            await using var mapping = new NpgsqlCommand("""
                INSERT INTO news_staging_publications (staging_item_id, publication_id)
                VALUES (@staging, @publication);
                """, db, tx);
            mapping.Parameters.AddWithValue("staging", item.Id);
            mapping.Parameters.AddWithValue("publication", publicationId);
            await mapping.ExecuteNonQueryAsync(ct);

            await using var queue = new NpgsqlCommand("""
                INSERT INTO news_processing_jobs (publication_id)
                VALUES (@publication)
                ON CONFLICT (publication_id) DO NOTHING;
                """, db, tx);
            queue.Parameters.AddWithValue("publication", publicationId);
            await queue.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        if (batch.Count > 0)
            logger.LogInformation("Promoted {Count} news items to Publications", batch.Count);
        return batch.Count;
    }
}
