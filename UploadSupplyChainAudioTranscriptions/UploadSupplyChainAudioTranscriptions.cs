using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UploadSupplyChainAudioTranscriptions.Entities;

namespace UploadSupplyChainAudioTranscriptions;

public class UploadSupplyChainAudioTranscriptions
{
    private readonly ILogger<UploadSupplyChainAudioTranscriptions> _logger;

    public UploadSupplyChainAudioTranscriptions(ILogger<UploadSupplyChainAudioTranscriptions> logger)
    {
        _logger = logger;
    }

    [Function("UploadSupplyChainAudioTranscriptions")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        _logger.LogInformation("Processing supply chain data upload.");

        if (!req.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            return new BadRequestObjectResult("Content-Type must be application/json.");
        }

        List<SupplyChainData>? dataList;
        try
        {
            dataList = await JsonSerializer.DeserializeAsync<List<SupplyChainData>>(req.Body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (dataList == null || dataList.Count == 0)
        {
            return new BadRequestObjectResult("At least one JSON payload is required.");
        }

        string? storageConnectionString = Environment.GetEnvironmentVariable("scaudiotranscriptions");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            _logger.LogError("connection string is null");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("SCAudioTranscriptions");
            await table.CreateIfNotExistsAsync();

            foreach (var data in dataList)
            {
                data.PartitionKey = data.SupplierID;
                data.RowKey = Guid.NewGuid().ToString();
                data.Timestamp = DateTimeOffset.UtcNow;

                var insertOperation = TableOperation.Insert(data);
                await table.ExecuteAsync(insertOperation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        return new OkObjectResult("Data uploaded successfully.");
    }
}