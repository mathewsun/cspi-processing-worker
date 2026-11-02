using Cspi.ProcessingWorker;

var builder = Host.CreateApplicationBuilder(args);
var localCountry=LocalCountrySettings.Read(builder.Configuration);
if(builder.Configuration.GetValue("LocalSemantic:AutoGroup",false)&&
   (!builder.Configuration.GetValue("LocalSemantic:Enabled",false)||!builder.Configuration.GetValue("ApprovedEvents:Enabled",false)))
    throw new InvalidOperationException("Automatic semantic grouping requires LocalSemantic and ApprovedEvents");
if(builder.Configuration.GetValue("LocalEvent:Enabled",false)&&(localCountry.Model!="spectrum-country:v1"||localCountry.Digest!="359d7dd4bcdab3d86b87d73ac27966f4dbb9f5efdfcc75d34a8764a09474fae7"))
    throw new InvalidOperationException("LocalEvent requires the pinned Qwen3 model for its versioned judgments");
if(builder.Configuration.GetValue("LocalEvent:Enabled",false)&&(!localCountry.Enabled||!builder.Configuration.GetValue("LocalSemantic:Enabled",false)))
    throw new InvalidOperationException("LocalEvent requires LocalSemantic and the shared LocalCountry inference scheduler");
if(builder.Configuration.GetValue("LocalSemantic:Enabled",false)&&new[]{"Semantic:Enabled","AutoEvents:Enabled","EventReview:Enabled"}.Any(k=>builder.Configuration.GetValue(k,false)))
    throw new InvalidOperationException("Disable legacy semantic/event proposal writers before enabling LocalSemantic");
if(builder.Configuration.GetValue("LocalDiscussion:Enabled",false)&&!localCountry.Enabled)
    throw new InvalidOperationException("LocalDiscussion requires the shared LocalCountry inference scheduler");
if(localCountry.Enabled && new[]{"CountryLlm:Enabled","CountryLlm:AutoEnqueue","CountryRoles:Enabled","CountryAuto:Enabled","CountryRelations:Enabled"}.Any(k=>builder.Configuration.GetValue(k,false)))
    throw new InvalidOperationException("Disable legacy country writers/queue before enabling LocalCountry");
builder.Services.AddSingleton(localCountry);
builder.Services.AddSingleton(new LocalCountryStore(builder.Configuration.GetConnectionString("DefaultConnection")??throw new InvalidOperationException("Database connection required"),localCountry.Revision,localCountry.Model,localCountry.Digest));
builder.Services.AddHttpClient("local-country",client=>{client.BaseAddress=new Uri("http://country-model:11434/");client.Timeout=TimeSpan.FromSeconds(180);client.MaxResponseContentBufferSize=65536;});
builder.Services.AddHostedService<LocalCountryQueueService>();
builder.Services.AddHostedService<LocalCountryService>();
builder.Services.AddHttpClient("local-semantic",client=>{client.BaseAddress=new Uri("http://semantic-model:8080/");client.Timeout=TimeSpan.FromSeconds(60);client.MaxResponseContentBufferSize=512000;});
builder.Services.AddHostedService<LocalSemanticService>();
if (builder.Configuration.GetValue("CountryRelations:Enabled", false) &&
    builder.Configuration.GetValue("PublicationGeography:Enabled", false))
    throw new InvalidOperationException(
        "CountryRelations and PublicationGeography cannot run simultaneously. Disable the legacy full-scan service.");

builder.Services.AddHttpClient("semantic", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<SemanticCandidateService>();
builder.Services.AddHostedService<EventReviewCandidateService>();
builder.Services.AddHostedService<AutoEventDecisionService>();
builder.Services.AddHostedService<ApprovedEventService>();
builder.Services.AddHostedService<SingletonEventService>();
builder.Services.AddHttpClient("country-llm", client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddHostedService<CountryLlmQueueService>();
builder.Services.AddHostedService<LlmCountryRoleService>();
builder.Services.AddHostedService<CountryRoleProposalService>();
builder.Services.AddHostedService<CountryRelationsMaterializer>();
builder.Services.AddHostedService<IncrementalPublicationGeographyService>();
builder.Services.AddHostedService<EventGeographyReconciliationService>();
builder.Services.AddHostedService<CountryAutoDecisionService>();
builder.Services.AddHostedService<ProcessingService>();
builder.Services.AddHostedService<StagingPromotionService>();
await builder.Build().RunAsync();
