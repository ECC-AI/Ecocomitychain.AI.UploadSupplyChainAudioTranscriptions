using ClosedXML.Excel;
using CsvHelper;
using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities;
using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using Neo4j.Driver;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
            var results = new List<SupplyChainDataViewModel>();
            TableContinuationToken? token = null;

            do
            {
                var segment = await table.ExecuteQuerySegmentedAsync(query, token);
                foreach (var item in segment.Results)
                {
                    results.Add(new SupplyChainDataViewModel
                    {
                        Tier = item.Tier,
                        SupplierID = item.SupplierID,
                        Stage = item.Stage,
                        Material = item.Material,
                        Status = item.Status,
                        BarColor = item.Status?.ToLowerInvariant() switch
                        {
                            "completed" => "Green",
                            "in progress" => "Yellow",
                            "delayed" => "Red",
                            "not started" => "Gray",
                            _ => "Blue"
                        },
                        QuantityPlanned = item.QtyPlanned.HasValue ? item.QtyPlanned : null,
                        QuantityFromInventory = item.QtyFromInventory.HasValue ? item.QtyFromInventory : null,
                        QuantityProcured = item.QtyProcured.HasValue ? item.QtyProcured : null,
                        QuantityProduced = item.QtyProduced.HasValue ? item.QtyProduced : null,
                        QuantityRemaining = item.QtyRemaining.HasValue ? item.QtyRemaining : null,
                        RippleEffect = item.RippleEffect,
                        Timestamp = item.ReportedTime
                    });
                }
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
                            data.ReportedTime = data.Timestamp;

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

    
    [Function("GetSupplyChainAudioTranscriptionsBySupplierId")]
    public async Task<IActionResult> GetBySupplierId(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "supplychain/supplier/{supplierId}")] HttpRequest req,
    string supplierId)
    {
        _logger.LogInformation($"Retrieving supply chain audio transcriptions for SupplierID: {supplierId}");

        if (string.IsNullOrWhiteSpace(supplierId))
        {
            return new BadRequestObjectResult("SupplierID is required.");
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

            var query = new TableQuery<SupplyChainData>().Where(
                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, supplierId)
            );

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

    [Function("QueryRawMaterialGraph")]
    public async Task<IActionResult> QueryNeo4jByRawMaterialAsync(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "rawmaterial/{rawMaterialName}")] HttpRequest req,
    string rawMaterialName)
    {
        if (string.IsNullOrWhiteSpace(rawMaterialName))
        {
            return new BadRequestObjectResult("Missing or invalid 'rawMaterialName' in route.");
        }

        var uri = "neo4j+s://7c2f46c2.databases.neo4j.io";
        var user = "neo4j";
        var password = "IX9e2lhJ09QPNzE4sTRdyKR28gB3VSJ6wG5n1ZbIsG4";

        var cypherQuery = $@"
            MATCH (COMPONENTRAWMATERIAL:ComponentRawMaterial {{Name: '{rawMaterialName}'}})<- [r1:COMP_MADEOF_RAWMAT]-(COMPONENT:Component)
                  <- [r2:HAS_COMPONENT]- (BOMSUBITEM:BomSubItem)
                  <- [r3:HAS_SUBASSEMBLY]-(BOMITEM:BomItem)
                  <- [r4:HAS_ASSEMBLY]-(MATERIALBOM:MaterialBOM)
            RETURN COMPONENTRAWMATERIAL, COMPONENT, BOMSUBITEM, BOMITEM, MATERIALBOM, r1, r2, r3, r4
        ";

        try
        {
            var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
            var session = driver.AsyncSession();
            var result = await session.RunAsync(cypherQuery);
            var records = await result.ToListAsync();

            var resultsList = new List<object>();
            foreach (var record in records)
            {
                resultsList.Add(new
                {
                    crm = record["COMPONENTRAWMATERIAL"].As<INode>().Properties,
                    c = record["COMPONENT"].As<INode>().Properties,
                    bsi = record["BOMSUBITEM"].As<INode>().Properties,
                    bi = record["BOMITEM"].As<INode>().Properties,
                    mb = record["MATERIALBOM"].As<INode>().Properties,
                    r1 = record["r1"].As<IRelationship>().Properties,
                    r2 = record["r2"].As<IRelationship>().Properties,
                    r3 = record["r3"].As<IRelationship>().Properties,
                    r4 = record["r4"].As<IRelationship>().Properties
                });
            }

            await session.CloseAsync();

            var tangledTreeData = BuildHierarchicalJson(resultsList);

            return new OkObjectResult(JsonSerializer.Serialize(tangledTreeData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Neo4j");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private object BuildHierarchicalJson(List<object> resultsList)
    {
        string GetName(IDictionary<string, object> node, string fallback = "Unknown")
        {
            if (node == null) return fallback;
            if (node.TryGetValue("Name", out var nameObj) && nameObj is string nameStr)
                return nameStr;
            if (node.TryGetValue("name", out var nameObj2) && nameObj2 is string nameStr2)
                return nameStr2;
            return fallback;
        }

        var plant = new Dictionary<string, object>
        {
            ["name"] = "Hyundai Chennai",
            ["children"] = new List<Dictionary<string, object>>()
        };

        var children = (List<Dictionary<string, object>>)plant["children"];

        foreach (dynamic record in resultsList)
        {
            var crm = record.crm as IDictionary<string, object>;
            var c = record.c as IDictionary<string, object>;
            var bsi = record.bsi as IDictionary<string, object>;
            var bi = record.bi as IDictionary<string, object>;
            var mb = record.mb as IDictionary<string, object>;


            string rawMatName = GetName(crm, "ComponentRawMaterial");
            string componentName = GetName(c, "Component");
            string subassemblyId = GetName(bsi, "BomSubItem");
            string assemblyName = GetName(bi, "BomItem");
            string mbName = GetName(mb, "MaterialBOM");

            var rawMatNode = new Dictionary<string, object>
            {
                ["relation"] = "COMP_MADEOF_RAWMAT",
                ["name"] = rawMatName
            };

            var componentNode = new Dictionary<string, object>
            {
                ["relation"] = "HAS_COMPONENT",
                ["name"] = componentName,
                ["children"] = new List<Dictionary<string, object>> { rawMatNode }
            };

            var subassemblyNode = new Dictionary<string, object>
            {
                ["relation"] = "HAS_SUBASSEMBLY",
                ["name"] = subassemblyId,
                ["children"] = new List<Dictionary<string, object>> { componentNode }
            };

            var assemblyNode = new Dictionary<string, object>
            {
                ["relation"] = "HAS_ASSEMBLY",
                ["name"] = assemblyName,
                ["children"] = new List<Dictionary<string, object>> { subassemblyNode }
            };

            children.Add(assemblyNode);
        }

        return plant;
    }

}