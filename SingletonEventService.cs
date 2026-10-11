using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Creates a provisional event for each processed article that has no event.
/// Does not classify geography or assert similarity. Runs independently of review.
/// </summary>
public sealed class SingletonEventService(
    IConfiguration configuration, ILogger<SingletonEventService> logger) : BackgroundService
{
    private readonly string connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Database connection is required");
    private readonly int batchSize = Math.Clamp(configuration.GetValue("SingletonEvents:BatchSize", 100), 1, 500);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("SingletonEvents:Enabled", false))
        {
            logger.LogInformation("Provisional singleton event creation disabled");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var count = await CreateBatch(ct);
                if (count > 0) logger.LogInformation("Created {Count} provisional events", count);
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provisional event creation failed");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }

    private async Task<int> CreateBatch(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Shared advisory lock with ApprovedEventService and EventCorrectionsController.
        // Guarantees a single event-graph writer across horizontally scaled instances.
        await using (var guard = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(23011, 7)", db, tx))
            await guard.ExecuteNonQueryAsync(ct);

        var ids = new List<int>();
        await using (var select = new NpgsqlCommand("""
            SELECT p."Id"
            FROM "Publications" p
            JOIN news_processing_jobs j ON j.publication_id=p."Id"
            WHERE j.processed_at IS NOT NULL
              AND j.processed_at < now() - interval '30 minutes'
              AND NOT EXISTS (
                  SELECT 1 FROM news_event_link_reviews r
                  WHERE r.decision IN ('Pending','Approved')
                    AND (r.publication_id_a=p."Id" OR r.publication_id_b=p."Id"))
              AND NOT EXISTS (
                  SELECT 1 FROM "EventPublications" ep WHERE ep."PublicationId"=p."Id")
            ORDER BY p."Id"
            LIMIT @limit
            FOR UPDATE OF p SKIP LOCKED;
            """, db, tx))
        {
            select.Parameters.AddWithValue("limit", batchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));
        }

        foreach (var id in ids)
        {
            await using var create = new NpgsqlCommand("""
                INSERT INTO "Events"
                    ("Name","Country","OccurredAt","Significance","SourceCertainty","BasisChanged")
                SELECT LEFT("Title",500), 'Unclassified', "PublishedAt",
                       'Средняя','Неизвестен',false
                FROM "Publications" WHERE "Id"=@id
                RETURNING "Id";
                """, db, tx);
            create.Parameters.AddWithValue("id", id);
            var eventId = (int)(await create.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Publication disappeared"));

            await using var link = new NpgsqlCommand("""
                INSERT INTO "EventPublications"
                    ("EventId","PublicationId","Similarity","Method","LinkedAt")
                VALUES (@event,@publication,1,'ProvisionalSingletonV1',now());
                """, db, tx);
            link.Parameters.AddWithValue("event", eventId);
            link.Parameters.AddWithValue("publication", id);
            await link.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return ids.Count;
    }
}
