using Cspi.ProcessingWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ProcessingService>();
builder.Services.AddHostedService<StagingPromotionService>();
await builder.Build().RunAsync();
