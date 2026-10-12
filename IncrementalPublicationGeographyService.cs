using Npgsql;

namespace Cspi.ProcessingWorker;

// Incremental publication-country reconciler; runs only when explicitly enabled.
public sealed class IncrementalPublicationGeographyService(
    IConfiguration config, ILogger<IncrementalPublicationGeographyService> logger) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Database connection required");
    private readonly int batchSize = Math.Clamp(config.GetValue("PublicationGeography:BatchSize", 100), 1, 500);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("PublicationGeography:Enabled", false)) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var count = await RunBatch(ct);
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publication geography batch failed");
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
        }
    }

    private async Task<int> RunBatch(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(23011,8)",db,tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            CREATE TEMP TABLE publication_batch ON COMMIT DROP AS
            SELECT publication_id,changed_at FROM news_publication_geography_dirty
            ORDER BY changed_at,publication_id LIMIT @limit FOR UPDATE SKIP LOCKED;
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("limit",batchSize);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("""
            CREATE TEMP TABLE publication_desired ON COMMIT DROP AS
            SELECT DISTINCT ON (p.publication_id,p.country_profile_id)
                p.publication_id,p.country_profile_id,
                CASE p.proposed_role WHEN 'Primary' THEN 0
                    WHEN 'Secondary' THEN 1 ELSE 2 END AS role,
                p.confidence
            FROM news_country_role_proposals p
            JOIN publication_batch b ON b.publication_id=p.publication_id
            WHERE p.status='Accepted'
              AND NOT EXISTS (
                SELECT 1 FROM news_geography_manual_overrides o
                WHERE o.target_type='Publication' AND o.target_id=p.publication_id
                  AND o.country_profile_id=p.country_profile_id)
              AND NOT EXISTS (
                  SELECT 1 FROM news_country_role_proposals veto
                  WHERE veto.publication_id=p.publication_id
                    AND veto.country_profile_id=p.country_profile_id
                    AND veto.status='Rejected')
            ORDER BY p.publication_id,p.country_profile_id,
                CASE p.proposed_role WHEN 'Primary' THEN 0
                    WHEN 'Secondary' THEN 1 ELSE 2 END,p.confidence DESC;
            """,db,tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM "PublicationCountries" pc
            USING publication_batch b, news_publication_country_ownership own
            WHERE pc."PublicationId"=b.publication_id
              AND own.publication_id=pc."PublicationId"
              AND own.country_profile_id=pc."CountryProfileId"
              AND own.owner='IncrementalPublicationGeographyV1'
              AND NOT EXISTS (
                SELECT 1 FROM publication_desired d
                WHERE d.publication_id=pc."PublicationId"
                  AND d.country_profile_id=pc."CountryProfileId"
                  AND d.role=pc."Role");
            """,db,tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            UPDATE "PublicationCountries" pc
            SET "Role"=d.role,"Confidence"=d.confidence,"ClassifiedAt"=now()
            FROM publication_desired d
            JOIN news_publication_country_ownership own
              ON own.publication_id=d.publication_id
             AND own.country_profile_id=d.country_profile_id
             AND own.owner='IncrementalPublicationGeographyV1'
            WHERE pc."PublicationId"=d.publication_id
              AND pc."CountryProfileId"=d.country_profile_id
              AND (pc."Role" IS DISTINCT FROM d.role
                OR pc."Confidence" IS DISTINCT FROM d.confidence);
            """,db,tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            WITH inserted AS (
                INSERT INTO "PublicationCountries"
                    ("PublicationId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
                SELECT publication_id,country_profile_id,role,confidence,'LlmRoleV1',now()
                FROM publication_desired
                WHERE NOT EXISTS (
                  SELECT 1 FROM news_geography_manual_overrides o
                  WHERE o.target_type='Publication' AND o.target_id=publication_id
                    AND o.country_profile_id=country_profile_id)
                ON CONFLICT ("PublicationId","CountryProfileId") DO NOTHING
                RETURNING "PublicationId","CountryProfileId"
            )
            INSERT INTO news_publication_country_ownership
                (publication_id,country_profile_id,owner)
            SELECT "PublicationId","CountryProfileId",'IncrementalPublicationGeographyV1'
            FROM inserted
            ON CONFLICT DO NOTHING;
            """,db,tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using var done = new NpgsqlCommand("""
            DELETE FROM news_publication_geography_dirty d USING publication_batch b
            WHERE d.publication_id=b.publication_id AND d.changed_at=b.changed_at;
            """,db,tx);
        var processed = await done.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return processed;
    }
}
