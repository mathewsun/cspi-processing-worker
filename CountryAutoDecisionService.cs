using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Conservative country role decision engine. Dry-run by default.
/// Only corroborated Mentioned proposals are eligible; Primary and Secondary
/// require a calibrated fact classifier, not merely an LLM confidence value.
/// </summary>
public sealed class CountryAutoDecisionService(
    IConfiguration config, ILogger<CountryAutoDecisionService> logger) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database connection");
    private readonly bool apply = config.GetValue("CountryAuto:Apply", false);
    private readonly int batch = Math.Clamp(config.GetValue("CountryAuto:BatchSize", 100), 1, 500);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("CountryAuto:Enabled", false)) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var count = await Evaluate(ct);
                if (count > 0)
                    logger.LogInformation("Evaluated {Count} country proposals; apply={Apply}", count, apply);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Country auto decision failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<int> Evaluate(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Do not override any prior human Accepted/Rejected decision.
        // Two independent signals: an explicit alias mention and an LLM proposal.
        // Never use raw LLM confidence as a calibrated probability.
        await using var cmd = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT p.publication_id, p.country_profile_id, p.method,
                       CASE WHEN p.proposed_role='Mentioned'
                                  AND p.confidence>=0.95
                                  AND EXISTS (
                                    SELECT 1 FROM news_country_candidates c
                                    WHERE c.publication_id=p.publication_id
                                      AND c.country_profile_id=p.country_profile_id
                                      AND c.method='MultilingualAliasV1')
                            THEN 'Eligible' ELSE 'Skipped' END AS decision,
                       CASE WHEN p.proposed_role='Mentioned'
                                  AND p.confidence>=0.95
                                  AND EXISTS (
                                    SELECT 1 FROM news_country_candidates c
                                    WHERE c.publication_id=p.publication_id
                                      AND c.country_profile_id=p.country_profile_id
                                      AND c.method='MultilingualAliasV1')
                            THEN 'Independent alias and LLM evidence'
                            ELSE 'Insufficient corroboration or unsupported role' END AS reason
                FROM news_country_role_proposals p
                WHERE p.status='Pending' AND p.method='LlmRoleV1'
                  AND NOT EXISTS (
                    SELECT 1 FROM news_country_auto_decisions d
                    WHERE d.publication_id=p.publication_id
                      AND d.country_profile_id=p.country_profile_id
                      AND d.method=p.method)
                ORDER BY p.created_at LIMIT @limit
            ), recorded AS (
                INSERT INTO news_country_auto_decisions
                    (publication_id,country_profile_id,method,decision,reason)
                SELECT publication_id,country_profile_id,method,
                       CASE WHEN decision='Eligible' AND @apply THEN 'Accepted'
                            ELSE decision END,reason
                FROM candidates
                ON CONFLICT DO NOTHING
                RETURNING publication_id,country_profile_id,method,decision
            ), accepted AS (
                UPDATE news_country_role_proposals p SET status='Accepted'
                FROM recorded r
                WHERE @apply AND r.decision='Accepted'
                  AND p.publication_id=r.publication_id
                  AND p.country_profile_id=r.country_profile_id
                  AND p.method=r.method AND p.status='Pending'
                  AND NOT EXISTS (
                    SELECT 1 FROM news_country_role_proposals human
                    WHERE human.publication_id=p.publication_id
                      AND human.country_profile_id=p.country_profile_id
                      AND human.status='Rejected')
                RETURNING p.publication_id
            )
            SELECT count(*) FROM recorded;
            """,db,tx);
        cmd.Parameters.AddWithValue("limit",batch);
        cmd.Parameters.AddWithValue("apply",apply);
        var count=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await tx.CommitAsync(ct);
        return count;
    }
}
