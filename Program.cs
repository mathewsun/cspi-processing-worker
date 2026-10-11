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
builder.Services.AddHostedService<CountryRoleProposalService>();
builder.Services.AddHostedService<CountryRelationsMaterializer>();
builder.Services.AddHostedService<ProcessingService>();
builder.Services.AddHostedService<StagingPromotionService>();
await builder.Build().RunAsync();
