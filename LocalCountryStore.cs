using Npgsql;
using System.Text.Json;

namespace Cspi.ProcessingWorker;

public sealed class LocalCountryStore(string connection,string revision,string model,string digest)
{
    public async Task Initialize(CancellationToken ct)
    {
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);
        await using var command=new NpgsqlCommand("""
            INSERT INTO news_country_ai_runs(model_id,model_name,model_digest,prompt_version,snapshot_max_id,live_cursor)
            SELECT @revision,@model,@digest,@prompt,coalesce(max("Id"),0),coalesce(max("Id"),0) FROM "Publications"
            ON CONFLICT DO NOTHING;
            """,db);
        command.Parameters.AddWithValue("revision",revision);command.Parameters.AddWithValue("model",model);
        command.Parameters.AddWithValue("digest",digest);command.Parameters.AddWithValue("prompt",LocalCountryClassifier.PromptVersion);
        await command.ExecuteNonQueryAsync(ct);
        await using var verify=new NpgsqlCommand("SELECT model_name=@model AND model_digest=@digest AND prompt_version=@prompt FROM news_country_ai_runs WHERE model_id=@revision",db);
        verify.Parameters.AddWithValue("revision",revision);verify.Parameters.AddWithValue("model",model);verify.Parameters.AddWithValue("digest",digest);verify.Parameters.AddWithValue("prompt",LocalCountryClassifier.PromptVersion);
        if(await verify.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("Queue revision belongs to another model/prompt");
    }
    public async Task<int> Enqueue(CancellationToken ct)
    {
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand("""
            WITH run AS (SELECT * FROM news_country_ai_runs WHERE model_id=@revision FOR UPDATE),
            archive AS (SELECT p."Id" FROM "Publications" p,run r WHERE p."Id">r.archive_cursor AND p."Id"<=r.snapshot_max_id ORDER BY p."Id" LIMIT 10000),
            live AS (SELECT p."Id" FROM "Publications" p,run r WHERE p."Id">r.live_cursor ORDER BY p."Id" LIMIT 10000),
            queued AS (
              INSERT INTO news_country_llm_jobs(publication_id,model_id,queue_kind)
              SELECT "Id",@revision,'Archive' FROM archive UNION ALL SELECT "Id",@revision,'Live' FROM live
              ON CONFLICT DO NOTHING RETURNING publication_id
            ), advanced AS (
              UPDATE news_country_ai_runs SET archive_cursor=coalesce((SELECT max("Id") FROM archive),snapshot_max_id),
                live_cursor=coalesce((SELECT max("Id") FROM live),live_cursor) WHERE model_id=@revision
              RETURNING model_id
            ) SELECT count(*) FROM queued;
            """,db,tx);
        command.Parameters.AddWithValue("revision",revision);
        var count=Convert.ToInt32(await command.ExecuteScalarAsync(ct));await tx.CommitAsync(ct);return count;
    }
    public async Task<List<AiCountry>> Countries(CancellationToken ct)
    {
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);
        await using var command=new NpgsqlCommand("SELECT \"Id\",\"Name\" FROM \"Countries\" ORDER BY \"Id\"",db);
        await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<AiCountry>();
        while(await reader.ReadAsync(ct))result.Add(new(reader.GetInt32(0),reader.GetString(1)));return result;
    }
    public async Task<AiArticle?> Claim(bool archive,CancellationToken ct)
    {
        var token=Guid.NewGuid().ToString("N");
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);
        foreach(var kind in new[]{archive?"Archive":"Live",archive?"Live":"Archive"})
        {
        await using var command=new NpgsqlCommand("""
            WITH due AS (
              SELECT publication_id FROM news_country_llm_jobs
              WHERE model_id=@revision AND queue_kind=@kind AND state<>'Completed' AND attempts<3 AND next_attempt_at<=now()
                AND (lease_until IS NULL OR lease_until<now())
              ORDER BY publication_id
              FOR UPDATE SKIP LOCKED LIMIT 1
            ), claimed AS (
              UPDATE news_country_llm_jobs j SET lease_owner=@token,lease_until=now()+interval '5 minutes',started_at=now()
              FROM due WHERE j.publication_id=due.publication_id AND j.model_id=@revision
              RETURNING j.publication_id
            ) SELECT p."Id",p."Title",p."OriginalText" FROM claimed c JOIN "Publications" p ON p."Id"=c.publication_id;
            """,db);
        command.Parameters.AddWithValue("revision",revision);command.Parameters.AddWithValue("kind",kind);command.Parameters.AddWithValue("token",token);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(await reader.ReadAsync(ct))return new(reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),token);
        }
        return null;
    }
    public async Task<bool> Complete(AiArticle article,IReadOnlyList<AiRelation> relations,CancellationToken ct)
    {
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(23011,8)",db,tx))await gate.ExecuteNonQueryAsync(ct);
        // Same order as article-update trigger: publication before job. Do not apply stale input.
        await using(var current=new NpgsqlCommand("SELECT \"Title\",\"OriginalText\" FROM \"Publications\" WHERE \"Id\"=@id FOR SHARE",db,tx))
        {
            current.Parameters.AddWithValue("id",article.Id);await using var reader=await current.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))return false;
            var actual=new AiArticle(article.Id,reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1),article.Lease);
            if(actual.Hash!=article.Hash)return false;
        }
        await using(var fence=new NpgsqlCommand("SELECT 1 FROM news_country_llm_jobs WHERE publication_id=@id AND model_id=@revision AND lease_owner=@lease AND lease_until>clock_timestamp() AND state<>'Completed' FOR UPDATE",db,tx))
        {
            AddJob(fence,article);if(await fence.ExecuteScalarAsync(ct) is null)return false;
        }
        await using(var clear=new NpgsqlCommand("DELETE FROM news_country_role_proposals WHERE publication_id=@id AND method=@method AND status<>'Rejected'",db,tx))
        {
            clear.Parameters.AddWithValue("id",article.Id);clear.Parameters.AddWithValue("method",LocalCountryClassifier.Method);await clear.ExecuteNonQueryAsync(ct);
        }
        foreach(var relation in relations)
        {
            await using var command=new NpgsqlCommand("""
                INSERT INTO news_country_role_proposals(publication_id,country_profile_id,proposed_role,confidence,method,evidence,status)
                SELECT @id,@country,@role,0.5,@method,@evidence,'Accepted'
                WHERE NOT EXISTS(SELECT 1 FROM news_geography_manual_overrides WHERE target_type='Publication' AND target_id=@id AND country_profile_id=@country)
                  AND NOT EXISTS(SELECT 1 FROM news_country_role_proposals WHERE publication_id=@id AND country_profile_id=@country AND status='Rejected')
                ON CONFLICT DO NOTHING;
                """,db,tx);
            command.Parameters.AddWithValue("id",article.Id);command.Parameters.AddWithValue("country",relation.Country);command.Parameters.AddWithValue("role",relation.Role);
            command.Parameters.AddWithValue("method",LocalCountryClassifier.Method);command.Parameters.AddWithValue("evidence",relation.Evidence);await command.ExecuteNonQueryAsync(ct);
        }
        var json=JsonSerializer.Serialize(relations);
        await using(var audit=new NpgsqlCommand("""
            INSERT INTO news_country_ai_results(publication_id,model_id,input_hash,input_scope,result_json)
            VALUES(@id,@revision,@hash,@scope,@json::jsonb) ON CONFLICT DO NOTHING;
            UPDATE news_country_llm_jobs SET state='Completed',processed_at=now(),input_hash=@hash,input_scope=@scope,result_json=@json::jsonb,
              last_error=NULL,lease_owner=NULL,lease_until=NULL WHERE publication_id=@id AND model_id=@revision AND lease_owner=@lease;
            """,db,tx))
        {
            AddJob(audit,article);audit.Parameters.AddWithValue("hash",article.Hash);audit.Parameters.AddWithValue("scope",article.Scope);audit.Parameters.AddWithValue("json",json);await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return true;
    }
    public async Task Fail(AiArticle article,string error,CancellationToken ct)
    {
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync(ct);
        await using var command=new NpgsqlCommand("""
            UPDATE news_country_llm_jobs SET state='Failed',attempts=attempts+1,last_error=@error,next_attempt_at=now()+interval '15 minutes',
              lease_owner=NULL,lease_until=NULL WHERE publication_id=@id AND model_id=@revision AND lease_owner=@lease;
            """,db);
        AddJob(command,article);command.Parameters.AddWithValue("error",LocalCountryClassifier.Limit(error,400));await command.ExecuteNonQueryAsync(ct);
    }
    private void AddJob(NpgsqlCommand command,AiArticle article)
    {
        command.Parameters.AddWithValue("id",article.Id);command.Parameters.AddWithValue("revision",revision);command.Parameters.AddWithValue("lease",article.Lease);
    }
}
