using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UploadSupplyChainAudioTranscriptions.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Register AzureTableService as a singleton for DI
builder.Services.AddSingleton<AzureTableService>(sp =>
{
    var connectionString = Environment.GetEnvironmentVariable("scaudiotranscriptions");
    return new AzureTableService(connectionString ?? string.Empty);
});

builder.Build().Run();
    