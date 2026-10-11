using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Applies only explicitly Approved pairs. Never auto-approves candidate matches.
/// A transaction-scoped advisory lock serializes event graph changes across replicas.
/// Conflicting event clusters are recorded for manual resolution, never merged blindly.
/// </summary>
public sealed class ApprovedEventService(
    IConfiguration configuration, ILogger<ApprovedEventService> logger) : BackgroundService
{
    private readonly string connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Database connection is required");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("ApprovedEvents:Enabled", false))
        {
            logger.LogInformation("Approved event materialization disabled");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var changed = await ApplyOne(ct);
                if (!changed) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Approved event materialization failed");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }

    private async Task<bool> ApplyOne(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Fixed advisory key. Transaction releases it on commit or rollback.
        await using (var guard = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(23011, 7)", db, tx))
            await guard.ExecuteNonQueryAsync(ct);

        int a, b;
        float score;
        await using (var command = new NpgsqlCommand("""
            SELECT r.publication_id_a, r.publication_id_b, r.score
            FROM news_event_link_reviews r
            WHERE r.decision='Approved'
              AND NOT EXISTS (
                SELECT 1 FROM news_event_review_applications x
                WHERE x.publication_id_a=r.publication_id_a
                  AND x.publication_id_b=r.publication_id_b)
            ORDER BY r.reviewed_at NULLS LAST, r.created_at
            LIMIT 1 FOR UPDATE OF r SKIP LOCKED;
            """, db, tx))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await tx.CommitAsync(ct);
                return false;
            }
            a = reader.GetInt32(0);
            b = reader.GetInt32(1);
            score = reader.GetFloat(2);
        }

        var eventA = await ExistingEvent(db, tx, a, ct);
        var eventB = await ExistingEvent(db, tx, b, ct);
        if (eventA.HasValue && eventB.HasValue && eventA != eventB)
        {
            await Record(db, tx, a, b, null, "Conflict",
                $"Existing event clusters {eventA} and {eventB} differ", ct);
            await tx.CommitAsync(ct);
            logger.LogWarning("Approved pair {A}/{B} spans two existing event clusters", a, b);
            return true;
        }

        var eventId = eventA ?? eventB ?? await CreateEvent(db, tx, a, ct);
        foreach (var publicationId in new[] { a, b })
        {
            await using var link = new NpgsqlCommand("""
                INSERT INTO "EventPublications"
                    ("EventId","PublicationId","Similarity","Method","LinkedAt")
                VALUES (@event,@publication,@score,'ApprovedPairV1',now())
                ON CONFLICT ("EventId","PublicationId") DO NOTHING;
                """, db, tx);
            link.Parameters.AddWithValue("event", eventId);
            link.Parameters.AddWithValue("publication", publicationId);
            link.Parameters.AddWithValue("score", score);
            await link.ExecuteNonQueryAsync(ct);
        }
        await Record(db, tx, a, b, eventId, "Linked", null, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Applied approved pair {A}/{B} to event {Event}", a, b, eventId);
        return true;
    }

    private static async Task<int?> ExistingEvent(
        NpgsqlConnection db, NpgsqlTransaction tx, int publicationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT "EventId" FROM "EventPublications"
            WHERE "PublicationId"=@id ORDER BY "EventId" LIMIT 1;
            """, db, tx);
        cmd.Parameters.AddWithValue("id", publicationId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int id ? id : null;
    }

    private static async Task<int> CreateEvent(
        NpgsqlConnection db, NpgsqlTransaction tx, int publicationId, CancellationToken ct)
    {
        // The Country field is a compatibility placeholder, not a verified geographic role.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "Events"
              ("Name","Country","OccurredAt","Significance","SourceCertainty","BasisChanged")
            SELECT LEFT("Title",500), 'Unclassified', "PublishedAt",
                   'Средняя','Неизвестен',false
            FROM "Publications" WHERE "Id"=@id
            RETURNING "Id";
            """, db, tx);
        cmd.Parameters.AddWithValue("id", publicationId);
        return (int)(await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Publication missing"));
    }

    private static async Task Record(
        NpgsqlConnection db, NpgsqlTransaction tx, int a, int b,
        int? eventId, string outcome, string? detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO news_event_review_applications
              (publication_id_a,publication_id_b,event_id,outcome,detail)
            VALUES (@a,@b,@event,@outcome,@detail)
            ON CONFLICT DO NOTHING;
            """, db, tx);
        cmd.Parameters.AddWithValue("a", a);
        cmd.Parameters.AddWithValue("b", b);
        cmd.Parameters.AddWithValue("event", (object?)eventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
