using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Optional semantic candidate generator. Does not merge events or assign countries.
/// Disabled by default. Uses a separately hosted OpenAI-compatible embedding endpoint.
/// </summary>
public sealed class SemanticCandidateService(
    IConfiguration config, IHttpClientFactory factory, ILogger<SemanticCandidateService> log) : BackgroundService
{
    private readonly string connection = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database connection");
    private readonly string model = config["Semantic:Model"] ?? "";
    private readonly string endpoint = config["Semantic:Endpoint"] ?? "";
    private readonly string apiKey = config["Semantic:ApiKey"] ?? "";
    private readonly int dimensions = Math.Clamp(config.GetValue("Semantic:Dimensions", 384), 8, 4096);
    private readonly int maxComparisons = Math.Clamp(config.GetValue("Semantic:CandidateLimit", 250), 10, 1000);
    private readonly double threshold = Math.Clamp(config.GetValue("Semantic:SimilarityThreshold", 0.88), 0.5, 0.999);
    private readonly string owner = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Semantic:Enabled", false))
        {
            log.LogInformation("Semantic embeddings disabled");
            return;
        }
        if (string.IsNullOrWhiteSpace(model) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Semantic:Model and HTTPS Semantic:Endpoint required");
        log.LogInformation("Semantic candidate generation enabled model={Model} dimensions={Dimensions}", model, dimensions);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var candidate = await Next(ct);
                if (candidate is null) { await Task.Delay(TimeSpan.FromSeconds(5), ct); continue; }
                await Process(candidate.Value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Semantic candidate loop failed");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task<(int Id, string Title, string? Body, DateTime Published)?> Next(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT p."Id", p."Title", p."OriginalText", p."PublishedAt"
            FROM "Publications" p
            JOIN news_processing_jobs j ON j.publication_id = p."Id"
            WHERE j.processed_at IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM news_semantic_embeddings e
                  WHERE e.publication_id=p."Id" AND e.model_id=@model)
              AND NOT EXISTS (
                  SELECT 1 FROM news_semantic_failures f
                  WHERE f.publication_id=p."Id" AND f.model_id=@model
                    AND f.next_attempt_at>now())
            ORDER BY p."Id" LIMIT 1;
            """, db);
        cmd.Parameters.AddWithValue("model", model);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt32(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDateTime(3));
    }

    private async Task Process((int Id, string Title, string? Body, DateTime Published) article, CancellationToken ct)
    {
        try
        {
            var input = article.Title + "\n" + (article.Body is null ? "" : article.Body[..Math.Min(article.Body.Length, 4000)]);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new { model, input })
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await factory.CreateClient("semantic").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var values = json.RootElement.GetProperty("data")[0].GetProperty("embedding")
                .EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (values.Length != dimensions || values.Any(x => !float.IsFinite(x)))
                throw new InvalidDataException("Embedding dimensions or values invalid");
            var norm = Math.Sqrt(values.Sum(x => (double)x * x));
            if (norm < 1e-8) throw new InvalidDataException("Zero embedding vector");
            for (var i = 0; i < values.Length; i++) values[i] = (float)(values[i] / norm);

            await using var db = new NpgsqlConnection(connection);
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            var matches = new List<(int Id, float[] Vector)>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT e.publication_id, e.embedding
                FROM news_semantic_embeddings e
                JOIN "Publications" p ON p."Id"=e.publication_id
                WHERE e.model_id=@model AND e.dimensions=@dimensions
                  AND p."Id"<>@id
                  AND p."PublishedAt" BETWEEN @from AND @to
                ORDER BY p."PublishedAt" DESC LIMIT @limit;
                """, db, tx))
            {
                cmd.Parameters.AddWithValue("model", model);
                cmd.Parameters.AddWithValue("dimensions", dimensions);
                cmd.Parameters.AddWithValue("id", article.Id);
                cmd.Parameters.AddWithValue("from", article.Published.AddHours(-24));
                cmd.Parameters.AddWithValue("to", article.Published.AddHours(24));
                cmd.Parameters.AddWithValue("limit", maxComparisons);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    matches.Add((reader.GetInt32(0), reader.GetFieldValue<float[]>(1)));
            }
            foreach (var match in matches)
            {
                if (match.Vector.Length != values.Length) continue;
                var cosine = 0d;
                for (var i = 0; i < values.Length; i++) cosine += values[i] * match.Vector[i];
                if (cosine < threshold) continue;
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO news_semantic_candidates
                    (publication_id_a, publication_id_b, model_id, cosine_similarity)
                    VALUES (@a,@b,@model,@score) ON CONFLICT DO NOTHING;
                    """, db, tx);
                cmd.Parameters.AddWithValue("a", Math.Min(article.Id, match.Id));
                cmd.Parameters.AddWithValue("b", Math.Max(article.Id, match.Id));
                cmd.Parameters.AddWithValue("model", model);
                cmd.Parameters.AddWithValue("score", (float)cosine);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO news_semantic_embeddings(publication_id,model_id,dimensions,embedding)
                VALUES (@id,@model,@dimensions,@embedding)
                ON CONFLICT (publication_id,model_id) DO NOTHING;
                """, db, tx))
            {
                cmd.Parameters.AddWithValue("id", article.Id);
                cmd.Parameters.AddWithValue("model", model);
                cmd.Parameters.AddWithValue("dimensions", dimensions);
                cmd.Parameters.AddWithValue("embedding", values);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            log.LogInformation("Embedded publication {Id}; compared {Count} candidates", article.Id, matches.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Embedding failed for publication {Id}", article.Id);
            await using var db = new NpgsqlConnection(connection);
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO news_semantic_failures(publication_id,model_id,attempts,next_attempt_at,last_error)
                VALUES (@id,@model,1,now()+interval '5 minutes',@error)
                ON CONFLICT (publication_id,model_id) DO UPDATE
                SET attempts=news_semantic_failures.attempts+1,
                    next_attempt_at=now()+interval '15 minutes',
                    last_error=excluded.last_error;
                """, db);
            cmd.Parameters.AddWithValue("id", article.Id);
            cmd.Parameters.AddWithValue("model", model);
            cmd.Parameters.AddWithValue("error", ex.Message[..Math.Min(ex.Message.Length, 400)]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
