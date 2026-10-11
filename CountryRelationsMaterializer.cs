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

        // One accepted proposal per country: prefer strongest role (Primary=0)
        // then highest confidence. No manual/past relationship is overwritten.
        await using var publications = new NpgsqlCommand("""
            WITH ranked AS (
                SELECT DISTINCT ON (publication_id,country_profile_id)
                    publication_id,country_profile_id,proposed_role,confidence,method
                FROM news_country_role_proposals
                WHERE status='Accepted'
                ORDER BY publication_id,country_profile_id,
                    CASE proposed_role
                        WHEN 'Primary' THEN 0 WHEN 'Secondary' THEN 1 ELSE 2 END,
                    confidence DESC
            )
            INSERT INTO "PublicationCountries"
                ("PublicationId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
            SELECT publication_id,country_profile_id,
                CASE proposed_role WHEN 'Primary' THEN 0
                    WHEN 'Secondary' THEN 1 ELSE 2 END,
                confidence,LEFT(method,32),now()
            FROM ranked
            ON CONFLICT ("PublicationId","CountryProfileId") DO NOTHING;
            """, db, tx);
        var articleCount = await publications.ExecuteNonQueryAsync(ct);

        // An event-country relationship can be inferred only from already
        // accepted publication-country links. Existing classifications win.
        // Restrict event aggregation to an event with a single linked article:
        // multi-article event roles require a separate consensus policy.
        await using var events = new NpgsqlCommand("""
            WITH singleton_events AS (
                SELECT "EventId" FROM "EventPublications"
                GROUP BY "EventId" HAVING count(*)=1
            )
            INSERT INTO "EventCountries"
                ("EventId","CountryProfileId","Role","Confidence","Method","ClassifiedAt")
            SELECT ep."EventId",pc."CountryProfileId",pc."Role",
                   pc."Confidence",'SingletonArticleV1',now()
            FROM "EventPublications" ep
            JOIN singleton_events se ON se."EventId"=ep."EventId"
            JOIN "PublicationCountries" pc ON pc."PublicationId"=ep."PublicationId"
            ON CONFLICT ("EventId","CountryProfileId") DO NOTHING;
            """, db, tx);
        var eventCount = await events.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (articleCount, eventCount);
    }
}
