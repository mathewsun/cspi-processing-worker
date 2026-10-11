using Cspi.ProcessingWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ProcessingService>();
await builder.Build().RunAsync();
