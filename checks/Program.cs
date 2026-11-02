using Cspi.ProcessingWorker;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
if(Environment.GetEnvironmentVariable("COUNTRY_AI_LIVE_FIXTURE") is string fixture)
{
    using var data=JsonDocument.Parse(await File.ReadAllTextAsync(fixture));
    var catalog=JsonSerializer.Deserialize<List<AiCountry>>(data.RootElement.GetProperty("Countries"))!;
    var client=new HttpClient {BaseAddress=new Uri("http://country-model:11434/"),Timeout=TimeSpan.FromSeconds(180),MaxResponseContentBufferSize=65536};
    var failures=0;
    foreach(var sample in data.RootElement.GetProperty("Articles").EnumerateArray())
    {
        var article=new AiArticle(sample.GetProperty("Id").GetInt32(),sample.GetProperty("Title").GetString()!,sample.GetProperty("Body").GetString(),"");
        var expected=sample.GetProperty("Expected").EnumerateArray().Select(x=>x.GetInt32()).Order().ToArray();
        var timer=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var answer=await LocalCountryClassifier.Infer(client,"spectrum-country:v1",catalog,article,default);
            var actual=answer.Where(x=>x.Role is "Primary" or "Secondary").Select(x=>x.Country).Order().ToArray();
            var passed=expected.SequenceEqual(actual);if(!passed)failures++;
            Console.WriteLine(JsonSerializer.Serialize(new{article.Id,Passed=passed,Seconds=timer.Elapsed.TotalSeconds,Expected=expected,Relations=answer}));
        }
        catch(Exception ex){failures++;Console.WriteLine(JsonSerializer.Serialize(new{article.Id,Passed=false,Error=ex.Message}));}
    }
    if(failures>0)Environment.ExitCode=1;return;
}
var cs=Environment.GetEnvironmentVariable("COUNTRY_AI_CHECK_CONNECTION")??throw new Exception("Isolated DB required");
if(new NpgsqlConnectionStringBuilder(cs).Database!="country_ai_checks")throw new Exception("Only country_ai_checks database allowed");
async Task Exec(string sql){await using var db=new NpgsqlConnection(cs);await db.OpenAsync();await using var cmd=new NpgsqlCommand(sql,db);await cmd.ExecuteNonQueryAsync();}
async Task<long> Count(string sql){await using var db=new NpgsqlConnection(cs);await db.OpenAsync();await using var cmd=new NpgsqlCommand(sql,db);return Convert.ToInt64(await cmd.ExecuteScalarAsync());}
await Exec("""
CREATE TABLE "Countries"("Id" int PRIMARY KEY,"Name" text NOT NULL);
CREATE TABLE "Publications"("Id" int PRIMARY KEY,"Title" text NOT NULL,"OriginalText" text);
CREATE TABLE "PublicationCountries"("PublicationId" int REFERENCES "Publications","CountryProfileId" int REFERENCES "Countries","Role" int,"Confidence" real,"Method" text,"ClassifiedAt" timestamptz,PRIMARY KEY("PublicationId","CountryProfileId"));
CREATE TABLE "EventCountries"("EventId" int,"CountryProfileId" int,"Role" int,"Confidence" real,"Method" text,"ClassifiedAt" timestamptz,PRIMARY KEY("EventId","CountryProfileId"));
INSERT INTO "Countries" VALUES(1,'Россия'),(2,'Китай'),(3,'Мексика'),(4,'Кыргызстан');
INSERT INTO "Publications" VALUES(1,'Россия и Китай подписали соглашение.',NULL),(2,'Гонка в Мексике.',NULL),(3,'В Бишкеке открылась школа.',NULL);
""");
foreach(var file in new[]{"009_country_role_proposals.sql","010_llm_country_jobs.sql","011_country_llm_leases.sql","014_incremental_publication_geography.sql","015_publication_geography_ownership.sql","017_manual_geography_override.sql","018_local_country_ai.sql"})await Exec(await File.ReadAllTextAsync("sql/"+file));
var store=new LocalCountryStore(cs,"check-v1","check-model","check-digest");await store.Initialize(default);await store.Enqueue(default);
Check(await Count("SELECT count(*) FROM news_country_llm_jobs WHERE queue_kind='Archive'")==3,"all archive articles, including title-only, enqueued");
await Exec("INSERT INTO \"Publications\" VALUES(4,'Новая новость о России.',NULL);");await store.Enqueue(default);await store.Enqueue(default);
Check(await Count("SELECT count(*) FROM news_country_llm_jobs")==4,"live discovery and enqueue idempotency");
var live=(await store.Claim(false,default))!;Check(live.Id==4,"live priority");
var archive=(await store.Claim(true,default))!;Check(archive.Id==1,"guaranteed archive slot");
var countries=await store.Countries(default);var segments=LocalCountryClassifier.Segments(archive);
var parsed=LocalCountryClassifier.Parse("""{"countries":[{"country":"Russia","role":"Mentioned","evidence":0},{"country":"Russia","role":"Primary","evidence":0},{"country":"Китай","role":"Primary","evidence":0}]}""",countries,segments);
Check(parsed.Count==2&&parsed[0].Role=="Primary","multiple countries and duplicate role normalization");
foreach(var bad in new[]{"""{"countries":[{"country":"Atlantis","role":"Primary","evidence":0}]}""","""{"countries":[{"country":"Russia","role":"Primary","evidence":99}]}""","""{"countries":[{"country":"Russia","role":"MadeUp","evidence":0}]}""","{"})
{var refused=false;try{LocalCountryClassifier.Parse(bad,countries,segments);}catch{refused=true;}Check(refused,"invalid model output refused");}
Check(await store.Complete(archive,parsed,default),"automatic accepted proposals and completed job");
Check(!await store.Complete(archive,parsed,default),"completed lease cannot commit twice");
var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:DefaultConnection",cs}}).Build();
var materializer=new IncrementalPublicationGeographyService(config,NullLogger<IncrementalPublicationGeographyService>.Instance);await materializer.RunBatch(default);
Check(await Count("SELECT count(*) FROM \"PublicationCountries\" WHERE \"Method\"='Qwen3CountryV1'")==2,"actual model provenance materialized");
await Exec("SELECT news_exclude_publication_country_manual(1,1,'operator'); SELECT news_set_publication_country_manual(1,2,2,'operator'); UPDATE \"Publications\" SET \"OriginalText\"='Обновленный текст о России и Китае.' WHERE \"Id\"=1;");
Check(await Count("SELECT count(*) FROM news_country_llm_jobs WHERE publication_id=1 AND state='Pending' AND lease_owner IS NULL")==1,"text enrichment requeues and revokes lease");
var again=(await store.Claim(true,default))!;Check(again.Id==1,"changed article reclassified");await store.Complete(again,parsed.Append(new AiRelation(3,"Secondary",0,segments[0])).ToArray(),default);await materializer.RunBatch(default);
Check(await Count("SELECT count(*) FROM \"PublicationCountries\" WHERE \"PublicationId\"=1 AND \"CountryProfileId\"=1")==0,"manual exclusion survives model reclassification");
Check(await Count("SELECT count(*) FROM \"PublicationCountries\" WHERE \"PublicationId\"=1 AND \"CountryProfileId\"=2 AND \"Role\"=2 AND \"Method\"='Manual'")==1,"manual role survives model reclassification");
Check(await Count("SELECT count(*) FROM \"PublicationCountries\" WHERE \"PublicationId\"=1 AND \"CountryProfileId\"=3 AND \"Method\"='Qwen3CountryV1'")==1,"manual correction of one country does not suppress other model countries");
Check(await Count("SELECT count(*) FROM news_country_ai_results WHERE publication_id=1")==2,"input version audit history retained");
await Exec("UPDATE \"Publications\" SET \"Title\"='Изменено во время обработки' WHERE \"Id\"=4;");
Check(!await store.Complete(live,[new(1,"Primary",0,"Новая новость о России.")],default),"stale input/in-flight lease fenced");
var replacement=(await store.Claim(false,default))!;Check(replacement.Id==4&&replacement.Lease!=live.Lease,"reclaim has fresh generation token");
await Exec("UPDATE news_country_llm_jobs SET lease_until=now()-interval '1 second' WHERE publication_id=4;");
Check(!await store.Complete(replacement,[],default),"expired lease cannot commit");
var newest=(await store.Claim(false,default))!;Check(await store.Complete(newest,[],default),"empty country result completes without invented country");
Check(await Count("SELECT count(*) FROM \"PublicationCountries\" WHERE \"PublicationId\"=4")==0,"abstention does not imply source/default country");
var retry=(await store.Claim(true,default))!;await store.Fail(retry,"fixture failure",default);
Check(await Count("SELECT count(*) FROM news_country_llm_jobs WHERE state='Failed' AND attempts=1 AND lease_owner IS NULL")==1,"retry state durable, lease released");
Check(LocalCountryClassifier.Segments(new(10,"Only title",null,"" )).Length==1,"title-only input supported");
await using(var delayed=new NpgsqlConnection(cs))
{
 await delayed.OpenAsync();await using var tx=await delayed.BeginTransactionAsync();
 await using(var insert=new NpgsqlCommand("INSERT INTO \"Publications\" VALUES(5,'Late committing news',NULL)",delayed,tx))await insert.ExecuteNonQueryAsync();
 await Exec("INSERT INTO \"Publications\" VALUES(6,'Earlier commit with higher ID',NULL)");
 await store.Enqueue(default);
 Check(await Count("SELECT live_cursor FROM news_country_ai_runs WHERE model_id='check-v1'")==6,"cursor can pass uncommitted lower ID");
 await tx.CommitAsync();
 Check(await Count("SELECT count(*) FROM news_country_llm_jobs WHERE publication_id=5 AND model_id='check-v1' AND queue_kind='Live'")==1,"late lower-ID commit enqueues transactionally without cursor rescan");
}
await using(var delayed=new NpgsqlConnection(cs))
{
 await delayed.OpenAsync();await using var tx=await delayed.BeginTransactionAsync();
 await using(var insert=new NpgsqlCommand("INSERT INTO \"Publications\" VALUES(7,'Pre-run uncommitted news',NULL)",delayed,tx))await insert.ExecuteNonQueryAsync();
 await Exec("INSERT INTO \"Publications\" VALUES(8,'Visible higher ID before new run',NULL)");
 var next=new LocalCountryStore(cs,"check-v2","check-model","check-digest");var initialize=next.Initialize(default);
 await Task.Delay(200);Check(!initialize.IsCompleted,"new-run snapshot waits for existing ingestion transactions");
 await tx.CommitAsync();await initialize;await next.Enqueue(default);
 Check(await Count("SELECT count(*) FROM news_country_llm_jobs WHERE publication_id=7 AND model_id='check-v2'")==1,"pre-run late commit included in archive snapshot");
}
Console.WriteLine("Country AI checks passed");
