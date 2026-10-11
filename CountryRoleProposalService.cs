using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Proposes country roles without writing final PublicationCountries/EventCountries.
/// A mention alone is NOT evidence of Primary or Secondary impact.
/// </summary>
public sealed class CountryRoleProposalService(
    IConfiguration config, ILogger<CountryRoleProposalService> logger) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Database connection is required");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("CountryRoles:Enabled", false))
        {
            logger.LogInformation("Country role proposal stage disabled");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var inserted = await Generate(ct);
                if (inserted > 0) logger.LogInformation("Generated {Count} country-role proposals", inserted);
                await Task.Delay(TimeSpan.FromMinutes(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Country role proposal stage failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<int> Generate(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT c.publication_id, c.country_profile_id,
                       MIN(c.evidence) AS evidence
                FROM news_country_candidates c
                JOIN news_processing_jobs j ON j.publication_id=c.publication_id
                WHERE j.processed_at IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM news_country_role_proposals p
                    WHERE p.publication_id=c.publication_id
                      AND p.country_profile_id=c.country_profile_id
                      AND p.method='ExplicitMentionV1')
                GROUP BY c.publication_id,c.country_profile_id
                ORDER BY c.publication_id,c.country_profile_id LIMIT 500
            )
            INSERT INTO news_country_role_proposals
                (publication_id,country_profile_id,proposed_role,confidence,method,evidence)
            SELECT publication_id,country_profile_id,'Mentioned',0.5,
                   'ExplicitMentionV1',evidence
            FROM candidates
            ON CONFLICT DO NOTHING;
            """, db);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
