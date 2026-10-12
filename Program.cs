using Cspi.ProcessingWorker;

var builder = Host.CreateApplicationBuilder(args);
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
