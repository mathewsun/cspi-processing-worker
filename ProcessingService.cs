using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Independent, at-least-once processing stage. Currently supports normalization,
/// exact duplicate fingerprints and queue bookkeeping, not semantic clustering.
/// </summary>
public sealed class ProcessingService(IConfiguration configuration, ILogger<ProcessingService> logger) : BackgroundService
{
    private readonly string connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");
    private readonly string owner = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly int parallelism = Math.Clamp(configuration.GetValue("Processor:Concurrency", 4), 1, 16);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Processing worker started with {Parallelism} consumers", parallelism);
        await Task.WhenAll(Enumerable.Range(0, parallelism).Select(_ => WorkLoop(ct)));
    }

    private async Task WorkLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var id = await ClaimAsync(ct);
                if (id is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                await ProcessAsync(id.Value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processing loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task<int?> ClaimAsync(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH next_job AS (
                SELECT publication_id
                FROM news_processing_jobs
                WHERE processed_at IS NULL
                  AND next_attempt_at <= now()
                  AND (lease_until IS NULL OR lease_until < now())
                ORDER BY next_attempt_at, publication_id
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE news_processing_jobs j
            SET lease_owner = @owner, lease_until = now() + interval '90 seconds',
                attempts = j.attempts + 1
            FROM next_job
            WHERE j.publication_id = next_job.publication_id
            RETURNING j.publication_id;
            """, db);
        command.Parameters.AddWithValue("owner", owner);
        return (int?)await command.ExecuteScalarAsync(ct);
    }

    private async Task ProcessAsync(int publicationId, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            string? url;
            string title;
            string? articleText;
            await using (var command = new NpgsqlCommand("""
                SELECT "Url", "Title", "OriginalText" FROM "Publications"
                WHERE "Id" = @id;
                """, db, tx))
            {
                command.Parameters.AddWithValue("id", publicationId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Publication missing");
                url = reader.IsDBNull(0) ? null : reader.GetString(0);
                title = reader.GetString(1);
                articleText = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            var normalizedUrl = NormalizeUrl(url);
            var normalizedTitle = NormalizeText(title);
            var textHash = string.IsNullOrWhiteSpace(articleText) ? null : Hash(NormalizeText(articleText));
            var urlHash = Hash(normalizedUrl ?? ("title:" + normalizedTitle));
            int? duplicate = null;

            await using (var command = new NpgsqlCommand("""
                SELECT publication_id FROM news_article_fingerprints
                WHERE publication_id <> @id AND (
                    url_hash = @urlHash OR (@textHash IS NOT NULL AND text_hash = @textHash)
                )
                ORDER BY publication_id LIMIT 1;
                """, db, tx))
            {
                command.Parameters.AddWithValue("id", publicationId);
                command.Parameters.AddWithValue("urlHash", urlHash);
                command.Parameters.AddWithValue("textHash", (object?)textHash ?? DBNull.Value);
                var match = await command.ExecuteScalarAsync(ct);
                duplicate = match is int id ? id : null;
            }
            await using (var command = new NpgsqlCommand("""
                INSERT INTO news_article_fingerprints
                    (publication_id, url_hash, text_hash, normalized_title, exact_duplicate_of)
                VALUES (@id, @urlHash, @textHash, @title, @duplicate)
                ON CONFLICT (publication_id) DO NOTHING;
                """, db, tx))
            {
                command.Parameters.AddWithValue("id", publicationId);
                command.Parameters.AddWithValue("urlHash", urlHash);
                command.Parameters.AddWithValue("textHash", (object?)textHash ?? DBNull.Value);
                command.Parameters.AddWithValue("title", normalizedTitle);
                command.Parameters.AddWithValue("duplicate", (object?)duplicate ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
            }
            await CandidateAnalysis.RecordCandidates(db, tx, publicationId, title, articleText, ct);
            await using (var command = new NpgsqlCommand("""
                UPDATE news_processing_jobs
                SET processed_at = now(), lease_until = NULL, lease_owner = NULL, last_error = NULL
                WHERE publication_id = @id AND lease_owner = @owner;
                """, db, tx))
            {
                command.Parameters.AddWithValue("id", publicationId);
                command.Parameters.AddWithValue("owner", owner);
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Lease expired or was reassigned");
            }
            await tx.CommitAsync(ct);
            logger.LogInformation("Processed article {Id} exactDuplicateOf={Duplicate}", publicationId, duplicate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed processing article {Id}", publicationId);
            await using var command = new NpgsqlCommand("""
                UPDATE news_processing_jobs
                SET lease_owner = NULL, lease_until = NULL, last_error = @error,
                    next_attempt_at = now() + interval '5 minutes'
                WHERE publication_id = @id AND lease_owner = @owner;
                """, db);
            command.Parameters.AddWithValue("id", publicationId);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("error", ex.Message[..Math.Min(ex.Message.Length, 500)]);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static string? NormalizeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Host.ToLowerInvariant() + (path.Length == 0 ? "/" : path);
    }

    private static string NormalizeText(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKC), @"\s+", " ");

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
