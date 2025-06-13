using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using UploadSupplyChainAudioTranscriptions.Entities;
using ClosedXML.Excel;
using CsvHelper;
using System.Globalization;

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

    [Function("GetSupplyChainAudioTranscriptions")]
    public async Task<IActionResult> Get(
    [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        _logger.LogInformation("Retrieving supply chain audio transcriptions.");

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

            var query = new TableQuery<SupplyChainData>();
            var results = new List<SupplyChainData>();
            TableContinuationToken? token = null;

            do
            {
                var segment = await table.ExecuteQuerySegmentedAsync(query, token);
                results.AddRange(segment.Results);
                token = segment.ContinuationToken;
            } while (token != null);

            return new OkObjectResult(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }


    [Function("ImportSupplyChainAudioTranscriptionsFromBlob")]
    public async Task<IActionResult> ImportFromBlob(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        _logger.LogInformation("Manually triggered import of supply chain data from blob container (CSV support).");

        string? storageConnectionString = Environment.GetEnvironmentVariable("scaudiotranscriptions");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            _logger.LogError("connection string is null");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference("audiotranscriptionbatchfiles");
            if (!await container.ExistsAsync())
            {
                _logger.LogError("Blob container does not exist.");
                return new NotFoundObjectResult("Blob container not found.");
            }

            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("SCAudioTranscriptions");
            await table.CreateIfNotExistsAsync();

            BlobContinuationToken? continuationToken = null;
            int totalRecords = 0;
            do
            {
                var resultSegment = await container.ListBlobsSegmentedAsync(null, true, BlobListingDetails.None, null, continuationToken, null, null);
                foreach (IListBlobItem item in resultSegment.Results)
                {
                    if (item is CloudBlockBlob blob)
                    {
                        _logger.LogInformation($"Processing blob: {blob.Name}");
                        if (!blob.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning($"Blob {blob.Name} is not a CSV file. Skipping.");
                            continue;
                        }

                        using var stream = await blob.OpenReadAsync();
                        if (stream.Length == 0)
                        {
                            _logger.LogWarning($"Blob {blob.Name} is empty. Skipping.");
                            continue;
                        }

                        using var reader = new StreamReader(stream);
                        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
                        csv.Context.RegisterClassMap<SupplyChainDataMap>();
                        //csv.Context.HeaderValidated = null; // Ignore missing headers
                        //csv.Context.MissingFieldFound = null; // Ignore missing fields

                        var records = csv.GetRecords<SupplyChainData>();
                        foreach (var data in records)
                        {
                            data.PartitionKey = data.SupplierID;
                            data.RowKey = Guid.NewGuid().ToString();
                            data.Timestamp = DateTimeOffset.UtcNow;

                            var insertOperation = TableOperation.Insert(data);
                            await table.ExecuteAsync(insertOperation);
                            totalRecords++;
                        }
                    }
                }
                continuationToken = resultSegment.ContinuationToken;
            } while (continuationToken != null);

            _logger.LogInformation($"Import complete. Total records inserted: {totalRecords}");
            return new OkObjectResult($"Data imported from blob(s) successfully. Total records inserted: {totalRecords}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing from blob to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}