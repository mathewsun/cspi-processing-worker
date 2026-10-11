using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Reconciles only worker-owned EventCountries links. Human/manual assignments
/// are never deleted or overwritten. Run only after accepted article geography exists.
/// </summary>
public sealed class EventGeographyReconciliationService(
    IConfiguration config, ILogger<EventGeographyReconciliationService> logger) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database connection");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("EventGeography:Enabled", false))
        {
            logger.LogInformation("Event geography reconciliation disabled");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (removed, upserted) = await Reconcile(ct);
                if (removed + upserted > 0)
                    logger.LogInformation("Event geography reconciled: removed={Removed} upserted={Upserted}",
                        removed, upserted);
                await Task.Delay(TimeSpan.FromMinutes(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event geography reconciliation failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<(int Removed, int Upserted)> Reconcile(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Same graph lock as ApprovedEventService, SingletonEventService,
        // and manual event split. The country materializer uses a separate
        // lock; this service only consumes its committed publication links.
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(23011, 7)", db, tx))
            await guard.ExecuteNonQueryAsync(ct);

        // Conservative consensus:
        // singleton: inherit the one accepted publication-country role;
        // multi-article: require agreement on the same role from >=2 distinct
        // source IDs, and no contradictory role from another linked article.
        // Do not classify events from a publisher's home country.
        await using (var build = new NpgsqlCommand("""
            CREATE TEMP TABLE event_country_desired ON COMMIT DROP AS
            WITH members AS (
                SELECT ep."EventId" AS event_id, ep."PublicationId" AS publication_id,
                       p."SourceId" AS source_id
                FROM "EventPublications" ep
                JOIN "Publications" p ON p."Id"=ep."PublicationId"
            ), totals AS (
                SELECT event_id, COUNT(*) AS articles
                FROM members GROUP BY event_id
            ), evidence AS (
                SELECT m.event_id, pc."CountryProfileId" AS country_id,
                       pc."Role" AS role, pc."Confidence" AS confidence,
                       m.source_id, m.publication_id
                FROM members m
                JOIN "PublicationCountries" pc ON pc."PublicationId"=m.publication_id
            ), consensus AS (
                SELECT e.event_id,e.country_id,MIN(e.role) AS role,
                       MIN(e.confidence) AS confidence,
                       COUNT(DISTINCT e.source_id) AS sources,
                       COUNT(DISTINCT e.role) AS role_variants,
                       COUNT(*) AS supporting_articles,
                       t.articles
                FROM evidence e
                JOIN totals t ON t.event_id=e.event_id
                GROUP BY e.event_id,e.country_id,t.articles
            )
            SELECT event_id,country_id,role,confidence,
                   CASE WHEN articles=1 THEN 'SingletonArticleV1'
                        ELSE 'EventConsensusV1' END::varchar(32) AS method
            FROM consensus
            WHERE (articles=1)
               OR (articles>1 AND sources>=2 AND role_variants=1
                   AND supporting_articles=articles);
            """, db, tx))
            await build.ExecuteNonQueryAsync(ct);

        // Remove only relationships owned by this automation that are now
        // stale due to event merge/split, rejected country links or new evidence.
        await using var remove = new NpgsqlCommand("""
            DELETE FROM "EventCountries" ec
            WHERE ec."Method" IN ('SingletonArticleV1','EventConsensusV1')
              AND NOT EXISTS (
                  SELECT 1 FROM event_country_desired d
                  WHERE d.event_id=ec."EventId"
                    AND d.country_id=ec."CountryProfileId"
                    AND d.role=ec."Role"
                    AND d.method=ec."Method");
            """, db, tx);
        var removed = await remove.ExecuteNonQueryAsync(ct);

        // Manual rows win on conflict; automatic rows are inserted only if
        // no relationship already exists for this event/country.
        await using var insert = new NpgsqlCommand("""
            INSERT INTO "EventCountries"
                ("EventId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
            SELECT event_id,country_id,role,confidence,method,now()
            FROM event_country_desired
            ON CONFLICT ("EventId","CountryProfileId") DO NOTHING;
            """, db, tx);
        var inserted = await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (removed, inserted);
    }
}
