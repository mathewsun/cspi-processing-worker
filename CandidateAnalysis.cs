using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Cspi.ProcessingWorker;

/// <summary>
/// Conservative candidate generation. Neither a country mention nor a similar title
/// proves an event-country relationship or a shared event.
/// </summary>
public static class CandidateAnalysis
{
    public static string Normalize(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),
            @"[^\p{L}\p{N}]+", " ").Trim();

    public static double TitleSimilarity(string first, string second)
    {
        var a = Normalize(first).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = Normalize(second).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (a.Count < 5 || b.Count < 5) return 0;
        var common = a.Count(b.Contains);
        return (double)common / (a.Count + b.Count - common);
    }

    public static IReadOnlyList<(int Id, string Name)> FindExplicitCountryMentions(
        string title, string? body, IEnumerable<(int Id, string Name)> countries)
    {
        // Avoid matching common short words or names embedded inside longer words.
        // Results are candidates ONLY. Country names such as Georgia can be ambiguous.
        var text = (title + " " + (body ?? "")[..Math.Min(body?.Length ?? 0, 4000)])
            .Normalize(NormalizationForm.FormKC);
        var matches = new List<(int, string)>();
        foreach (var country in countries)
        {
            if (country.Name.Length < 4) continue;
            if (Regex.IsMatch(text,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(country.Name)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
                matches.Add(country);
        }
        return matches;
    }

    public static async Task RecordCandidates(
        NpgsqlConnection db, NpgsqlTransaction tx, int publicationId,
        string title, string? body, CancellationToken ct)
    {
        var countries = new List<(int Id, string Name)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT "Id", "Name" FROM "Countries";
            """, db, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                countries.Add((reader.GetInt32(0), reader.GetString(1)));

        foreach (var country in FindExplicitCountryMentions(title, body, countries))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO news_country_candidates
                    (publication_id,country_profile_id,method,evidence,confidence)
                VALUES (@publication,@country,'ExplicitNameV1',@evidence,0.5)
                ON CONFLICT DO NOTHING;
                """, db, tx);
            cmd.Parameters.AddWithValue("publication", publicationId);
            cmd.Parameters.AddWithValue("country", country.Id);
            cmd.Parameters.AddWithValue("evidence", country.Name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Candidate search bounded by time and count; no unbounded pairwise scan.
        var otherTitles = new List<(int Id, string Title)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT p."Id", p."Title" FROM "Publications" p
            WHERE p."Id" <> @id
              AND p."PublishedAt" >= (
                  SELECT "PublishedAt" - interval '24 hours'
                  FROM "Publications" WHERE "Id" = @id)
              AND p."PublishedAt" <= (
                  SELECT "PublishedAt" + interval '24 hours'
                  FROM "Publications" WHERE "Id" = @id)
            ORDER BY p."PublishedAt" DESC LIMIT 250;
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("id", publicationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                otherTitles.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        foreach (var other in otherTitles)
        {
            var similarity = TitleSimilarity(title, other.Title);
            if (similarity < 0.8) continue;
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO news_near_duplicate_candidates
                    (publication_id_a,publication_id_b,similarity,method)
                VALUES (@a,@b,@score,'TitleTokensV1')
                ON CONFLICT DO NOTHING;
                """, db, tx);
            cmd.Parameters.AddWithValue("a", Math.Min(publicationId, other.Id));
            cmd.Parameters.AddWithValue("b", Math.Max(publicationId, other.Id));
            cmd.Parameters.AddWithValue("score", (float)similarity);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
