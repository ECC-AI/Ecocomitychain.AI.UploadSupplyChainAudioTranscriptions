using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.IO;
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

        if (!req.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            return new BadRequestObjectResult("Content-Type must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(req.ContentType).Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return new BadRequestObjectResult("Missing content-type boundary.");
        }

        var reader = new MultipartReader(boundary, req.Body);
        MultipartSection section;
        Stream fileStream = null;

        while ((section = await reader.ReadNextSectionAsync()) != null)
        {
            var hasContentDispositionHeader =
                ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition);

            if (hasContentDispositionHeader && contentDisposition.DispositionType.Equals("form-data") &&
                contentDisposition.Name.Value == "file")
            {
                // Save the file stream for further processing
                fileStream = new MemoryStream();
                await section.Body.CopyToAsync(fileStream);
                fileStream.Position = 0;
                break;
            }
        }

        if (fileStream == null)
        {
            return new BadRequestObjectResult("No file found in the request.");
        }

        SupplyChainData? data;
        try
        {
            data = await JsonSerializer.DeserializeAsync<SupplyChainData>(fileStream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (data == null)
        {
            return new BadRequestObjectResult("No data found in JSON.");
        }

        // Set PartitionKey and RowKey
        data.PartitionKey = data.SupplierID;
        data.RowKey = Guid.NewGuid().ToString();
        data.Timestamp = DateTimeOffset.UtcNow;

        // Azure Table Storage
        string? storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("SupplyChainAudioTranscriptions");
            await table.CreateIfNotExistsAsync();

            var insertOperation = TableOperation.Insert(data);
            await table.ExecuteAsync(insertOperation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        return new OkObjectResult("Data uploaded successfully.");
    }
}