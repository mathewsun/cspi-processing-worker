using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Copies explicitly Accepted geographic proposals into CSPI relationship tables.
/// Rejected/Pending proposals never become authoritative links.
/// Existing manually classified relationships are not overwritten.
/// </summary>
public sealed class CountryRelationsMaterializer(
    IConfiguration config, ILogger<CountryRelationsMaterializer> logger) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database connection");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("CountryRelations:Enabled", false))
        {
            logger.LogInformation("Country relationship materialization disabled");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (articles, events) = await Materialize(ct);
                if (articles + events > 0)
                    logger.LogInformation("Country relationships: {Articles} publications, {Events} events", articles, events);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Country relationship materialization failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<(int Articles, int Events)> Materialize(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(23011, 8)", db, tx))
            await guard.ExecuteNonQueryAsync(ct);

        // Reconcile only rows explicitly owned by this service. Legacy rows and
        // manual classifications remain untouched. Accepted proposals are the
        // sole source of truth for this worker-owned subset.
        await using (var build = new NpgsqlCommand("""
            CREATE TEMP TABLE publication_country_desired ON COMMIT DROP AS
            SELECT DISTINCT ON (publication_id,country_profile_id)
                   publication_id,country_profile_id,
                   CASE proposed_role WHEN 'Primary' THEN 0
                        WHEN 'Secondary' THEN 1 ELSE 2 END AS role,
                   confidence
            FROM news_country_role_proposals
            WHERE status='Accepted'
              AND NOT EXISTS (
                  SELECT 1 FROM news_country_role_proposals veto
                  WHERE veto.publication_id=news_country_role_proposals.publication_id
                    AND veto.country_profile_id=news_country_role_proposals.country_profile_id
                    AND veto.status='Rejected')
            ORDER BY publication_id,country_profile_id,
                     CASE proposed_role WHEN 'Primary' THEN 0
                          WHEN 'Secondary' THEN 1 ELSE 2 END,
                     confidence DESC;
            """, db, tx))
            await build.ExecuteNonQueryAsync(ct);

        // Previously inserted worker rows may have Method set to a proposal
        // method such as LlmRoleV1 or ExplicitMentionV1. Restrict deletion to
        // these known automation identifiers; do not delete Manual/other rows.
        await using var remove = new NpgsqlCommand("""
            DELETE FROM "PublicationCountries" pc
            WHERE pc."Method" IN ('LlmRoleV1','ExplicitMentionV1')
              AND NOT EXISTS (
                  SELECT 1 FROM publication_country_desired d
                  WHERE d.publication_id=pc."PublicationId"
                    AND d.country_profile_id=pc."CountryProfileId"
                    AND d.role=pc."Role");
            """, db, tx);
        var removed = await remove.ExecuteNonQueryAsync(ct);

        // Revisions of the same worker-owned relation can change role/confidence.
        // Manual assignments take precedence and are not updated.
        await using var update = new NpgsqlCommand("""
            UPDATE "PublicationCountries" pc
            SET "Role"=d.role, "Confidence"=d.confidence, "ClassifiedAt"=now()
            FROM publication_country_desired d
            WHERE pc."PublicationId"=d.publication_id
              AND pc."CountryProfileId"=d.country_profile_id
              AND pc."Method" IN ('LlmRoleV1','ExplicitMentionV1')
              AND (pc."Role" IS DISTINCT FROM d.role
                   OR pc."Confidence" IS DISTINCT FROM d.confidence);
            """, db, tx);
        var updated = await update.ExecuteNonQueryAsync(ct);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO "PublicationCountries"
                ("PublicationId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
            SELECT publication_id,country_profile_id,role,confidence,
                   'LlmRoleV1',now()
            FROM publication_country_desired
            ON CONFLICT ("PublicationId","CountryProfileId") DO NOTHING;
            """, db, tx);
        var inserted = await insert.ExecuteNonQueryAsync(ct);
        var articleCount = removed + updated + inserted;

        // EventGeographyReconciliationService is the only event-country writer.
        var eventCount = 0;
        await tx.CommitAsync(ct);
        return (articleCount, eventCount);
    }
}
