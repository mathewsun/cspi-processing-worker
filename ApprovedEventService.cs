using Npgsql;
using System.Text.Json;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Materializes approved pairs; optional semantic grouping approves strong matches automatically.
/// A transaction-scoped advisory lock serializes event graph changes across replicas.
/// Split blocks and operator changes protect clusters from automatic merging.
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

    public async Task<bool> ApplyOne(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Fixed advisory key. Transaction releases it on commit or rollback.
        await using (var guard = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(23011, 7)", db, tx))
            await guard.ExecuteNonQueryAsync(ct);

        if (configuration.GetValue("LocalSemantic:AutoGroup", false))
            await SemanticAutoEvents.Promote(db,tx,ct);

        int a, b;
        float score;
        string evidence;
        var localGuard=configuration.GetValue("LocalSemantic:Enabled",false)?"""
            AND (coalesce(r.evidence->>'method','') NOT IN('LocalEventJudgeV1','LocalSemanticAutoV1') OR r.evidence->>'human_decision'='true' OR EXISTS(
              SELECT 1 FROM news_local_semantic_vectors va JOIN news_local_semantic_vectors vb ON vb.publication_id=r.publication_id_b AND vb.model_revision=va.model_revision
              JOIN news_local_semantic_jobs ja ON ja.publication_id=va.publication_id AND ja.model_revision=va.model_revision AND ja.state='Completed' AND ja.dirty_generation=ja.observed_generation
              JOIN news_local_semantic_jobs jb ON jb.publication_id=vb.publication_id AND jb.model_revision=vb.model_revision AND jb.state='Completed' AND jb.dirty_generation=jb.observed_generation
              WHERE va.publication_id=r.publication_id_a AND va.model_revision=r.evidence->>'embedding_model' AND va.input_hash=r.evidence->>'hash_a' AND vb.input_hash=r.evidence->>'hash_b'))
            """:"AND (coalesce(r.evidence->>'method','') NOT IN('LocalEventJudgeV1','LocalSemanticAutoV1') OR r.evidence->>'human_decision'='true')";
        await using (var command = new NpgsqlCommand($$"""
            SELECT r.publication_id_a, r.publication_id_b, r.score,r.evidence::text
            FROM news_event_link_reviews r
            WHERE r.decision='Approved'
              AND NOT EXISTS (
                SELECT 1 FROM news_event_pair_blocks block
                WHERE block.publication_id_a=r.publication_id_a
                  AND block.publication_id_b=r.publication_id_b)
              AND NOT EXISTS (
                SELECT 1 FROM news_event_review_applications x
                WHERE x.publication_id_a=r.publication_id_a
                  AND x.publication_id_b=r.publication_id_b)
            {{localGuard}}
            ORDER BY r.reviewed_at NULLS LAST, r.created_at
            LIMIT 1 FOR UPDATE OF r SKIP LOCKED;
            """, db, tx))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return false;
            }
            a = reader.GetInt32(0);
            b = reader.GetInt32(1);
            score = reader.GetFloat(2);
            evidence=reader.GetString(3);
        }

        using(var json=JsonDocument.Parse(evidence))
        {
            var e=json.RootElement;
            if(e.TryGetProperty("method",out var method)&&(method.GetString() is "LocalEventJudgeV1" or "LocalSemanticAutoV1")&&
                !(e.TryGetProperty("human_decision",out var human)&&human.ValueKind==JsonValueKind.True)&&
                !await LocalEventStore.Current(db,tx,a,b,e.GetProperty("hash_a").GetString()!,e.GetProperty("hash_b").GetString()!,ct))return false;
        }

        var eventA = await ExistingEvent(db, tx, a, ct);
        var eventB = await ExistingEvent(db, tx, b, ct);
        if(await ClusterBlocked(db,tx,a,b,eventA,eventB,ct))
        {
            await Record(db,tx,a,b,null,"Conflict","A manual rejection or split blocks cluster membership",ct);
            await tx.CommitAsync(ct);return true;
        }
        if (eventA.HasValue && eventB.HasValue && eventA != eventB)
        {
            // Automatic groups can merge; operator-owned groups remain protected.
            var merged = configuration.GetValue("LocalSemantic:AutoGroup",false) && await TryMergeAutomatic(db,tx,eventA.Value,eventB.Value,ct);
            if (!merged) merged = await TryMergeProvisional(db, tx, eventA.Value, eventB.Value, ct);
            if (!merged)
            {
                await Record(db, tx, a, b, null, "Conflict",
                    $"Existing event clusters {eventA} and {eventB} differ", ct);
                await tx.CommitAsync(ct);
                logger.LogWarning("Approved pair {A}/{B} spans protected event clusters", a, b);
                return true;
            }
            eventB = eventA;
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

    private static async Task<bool> ClusterBlocked(NpgsqlConnection db,NpgsqlTransaction tx,int a,int b,int? eventA,int? eventB,CancellationToken ct)
    {
        await using var cmd=LocalSemanticStore.Cmd(db,tx,"""
          WITH left_members AS(SELECT @a::int AS id UNION SELECT "PublicationId" FROM "EventPublications" WHERE "EventId"=@ea),
               right_members AS(SELECT @b::int AS id UNION SELECT "PublicationId" FROM "EventPublications" WHERE "EventId"=@eb)
          SELECT EXISTS(SELECT 1 FROM left_members l CROSS JOIN right_members r WHERE l.id<>r.id AND (
            EXISTS(SELECT 1 FROM news_event_pair_blocks x WHERE x.publication_id_a=LEAST(l.id,r.id) AND x.publication_id_b=GREATEST(l.id,r.id)) OR
            EXISTS(SELECT 1 FROM news_event_link_reviews x WHERE x.publication_id_a=LEAST(l.id,r.id) AND x.publication_id_b=GREATEST(l.id,r.id) AND x.decision='Rejected')));
          """,("a",a),("b",b),("ea",eventA),("eb",eventB));return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> TryMergeAutomatic(NpgsqlConnection db,NpgsqlTransaction tx,int target,int source,CancellationToken ct)
    {
        // Keep operator changes. Cross-cluster split/rejection checks ran under this graph lock.
        await using var safe=LocalSemanticStore.Cmd(db,tx,"""
          SELECT NOT EXISTS(SELECT 1 FROM "EventPublications" ep WHERE ep."EventId" IN(@source,@target)
            AND (ep."Method" NOT IN('ApprovedPairV1','ProvisionalSingletonV1') OR NOT EXISTS(
              SELECT 1 FROM news_event_review_applications a JOIN news_event_link_reviews r
                ON r.publication_id_a=a.publication_id_a AND r.publication_id_b=a.publication_id_b
              WHERE a.event_id=ep."EventId" AND a.outcome='Linked' AND r.evidence->>'method' IN('LocalSemanticAutoV1','LocalEventJudgeV1')
                AND coalesce(r.evidence->>'human_decision','false')<>'true')))
           AND NOT EXISTS(SELECT 1 FROM news_event_actions WHERE event_id IN(@source,@target) AND actor<>'processing-worker')
           AND NOT EXISTS(SELECT 1 FROM news_event_link_reviews r JOIN "EventPublications" ep
             ON ep."PublicationId" IN(r.publication_id_a,r.publication_id_b)
             WHERE ep."EventId" IN(@source,@target) AND r.evidence->>'human_decision'='true')
           AND NOT EXISTS(SELECT 1 FROM "Publications" WHERE "EventId" IN(@source,@target))
           AND NOT EXISTS(SELECT 1 FROM "EventCountries" ec WHERE ec."EventId" IN(@source,@target) AND NOT EXISTS(
             SELECT 1 FROM news_event_country_ownership own WHERE own.event_id=ec."EventId" AND own.country_profile_id=ec."CountryProfileId" AND own.owner='EventGeographyReconciliationV1'));
          """,("source",source),("target",target));
        if(await safe.ExecuteScalarAsync(ct) is not true)return false;
        await using var move=LocalSemanticStore.Cmd(db,tx,"""
          INSERT INTO "EventPublications"("EventId","PublicationId","Similarity","Method","LinkedAt")
            SELECT @target,"PublicationId","Similarity","Method",now() FROM "EventPublications" WHERE "EventId"=@source ON CONFLICT DO NOTHING;
          DELETE FROM "EventPublications" WHERE "EventId"=@source;
          DELETE FROM "EventCountries" WHERE "EventId"=@source;
          UPDATE news_event_review_applications SET event_id=@target WHERE event_id=@source;
          INSERT INTO news_event_actions(event_id,action,actor,details) VALUES(@target,'AutoMergeSemantic','processing-worker',jsonb_build_object('source_event_id',@source));
          """,("source",source),("target",target));await move.ExecuteNonQueryAsync(ct);return true;
    }

    private static async Task<bool> TryMergeProvisional(
        NpgsqlConnection db, NpgsqlTransaction tx, int targetEvent, int sourceEvent,
        CancellationToken ct)
    {
        // Refuse if the target/source contain any manually blocked pair.
        // Refuse if the source is not an untouched singleton, or has country
        // classification or legacy references. Retain source Event for audit.
        await using var cmd = new NpgsqlCommand("""
            WITH source_members AS (
                SELECT "PublicationId" FROM "EventPublications" WHERE "EventId"=@source
            ), target_members AS (
                SELECT "PublicationId" FROM "EventPublications" WHERE "EventId"=@target
            )
            SELECT
                (SELECT count(*) FROM source_members)=1
                AND (SELECT count(*) FROM "EventPublications"
                     WHERE "EventId"=@source AND "Method"='ProvisionalSingletonV1')=1
                AND NOT EXISTS (SELECT 1 FROM "EventCountries" WHERE "EventId"=@source)
                AND NOT EXISTS (SELECT 1 FROM "Publications" WHERE "EventId"=@source)
                AND NOT EXISTS (
                    SELECT 1 FROM news_event_pair_blocks b
                    JOIN source_members s ON b.publication_id_a=s."PublicationId"
                        OR b.publication_id_b=s."PublicationId"
                    JOIN target_members t ON
                        (b.publication_id_a=LEAST(s."PublicationId",t."PublicationId")
                         AND b.publication_id_b=GREATEST(s."PublicationId",t."PublicationId")))
                AND NOT EXISTS (
                    SELECT 1 FROM news_event_link_reviews r
                    JOIN source_members s ON r.publication_id_a=s."PublicationId"
                        OR r.publication_id_b=s."PublicationId"
                    JOIN target_members t ON
                        (r.publication_id_a=LEAST(s."PublicationId",t."PublicationId")
                         AND r.publication_id_b=GREATEST(s."PublicationId",t."PublicationId"))
                    WHERE r.decision='Rejected');
            """, db, tx);
        cmd.Parameters.AddWithValue("source", sourceEvent);
        cmd.Parameters.AddWithValue("target", targetEvent);
        if (await cmd.ExecuteScalarAsync(ct) is not bool safe || !safe)
            return false;

        await using (var move = new NpgsqlCommand("""
            UPDATE "EventPublications" SET "EventId"=@target
            WHERE "EventId"=@source;
            """, db, tx))
        {
            move.Parameters.AddWithValue("target", targetEvent);
            move.Parameters.AddWithValue("source", sourceEvent);
            if (await move.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        await using (var history = new NpgsqlCommand("""
            INSERT INTO news_event_actions(event_id,action,actor,details)
            VALUES (@target,'AutoMergeProvisional','processing-worker',
                    jsonb_build_object('source_event_id',@source,'target_event_id',@target));
            """, db, tx))
        {
            history.Parameters.AddWithValue("target", targetEvent);
            history.Parameters.AddWithValue("source", sourceEvent);
            await history.ExecuteNonQueryAsync(ct);
        }
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
