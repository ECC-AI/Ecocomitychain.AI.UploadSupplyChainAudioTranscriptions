using Azure;
using CsvHelper;
using CsvHelper.Configuration;
using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities;
using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Map;
using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.Table;
using Neo4j.Driver;
using Newtonsoft.Json;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UploadSupplyChainAudioTranscriptions.Entities;
using UploadSupplyChainAudioTranscriptions.Services;



namespace UploadSupplyChainAudioTranscriptions;


public class UploadSupplyChainAudioTranscriptions
{
    private readonly ILogger<UploadSupplyChainAudioTranscriptions> _logger;
    private readonly AzureTableService _tableService;
    private readonly ICosmosDbService _cosmosDbService;

    public UploadSupplyChainAudioTranscriptions(
        ILogger<UploadSupplyChainAudioTranscriptions> logger,
        AzureTableService tableService,
        ICosmosDbService cosmosDbService)
    {
        _logger = logger;
        _tableService = tableService;
        _cosmosDbService = cosmosDbService;
    }

    [Function("UploadSupplyChainAudioTranscriptions")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        _logger.LogInformation("Processing supply chain data upload.");

        if (!req.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            return new BadRequestObjectResult("Content-Type must be application/json. Multipart/form-data is not supported.");
        }

        string rawBody;
        using (var reader = new StreamReader(req.Body))
        {
            rawBody = await reader.ReadToEndAsync();
        }
        _logger.LogInformation($"Raw request body: {rawBody}");


        List<SupplyChainData>? dataList;
        try
        {
            dataList = JsonConvert.DeserializeObject<List<SupplyChainData>>(rawBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to deserialize JSON. Raw body: {rawBody}");
            return new BadRequestObjectResult($"Invalid JSON format: {ex.Message}");
        }

        if (dataList == null || dataList.Count == 0)
        {
            return new BadRequestObjectResult("At least one JSON payload is required.");
        }

        // For each SupplyChainData, query OemSupplierMapping and set OemPart
        foreach (var data in dataList)
        {
            string? supplierPartNumber = null;
            if (data.SupplierPart != null && !string.IsNullOrWhiteSpace(data.SupplierPart.SupplierPartNumber))
            {
                supplierPartNumber = data.SupplierPart.SupplierPartNumber;
            }
            // fallback: try to get from SupplierPartJson if not already set
            if (string.IsNullOrWhiteSpace(supplierPartNumber) && !string.IsNullOrWhiteSpace(data.SupplierPartJson))
            {
                try
                    {
                    var part = JsonConvert.DeserializeObject<SupplierPart>(data.SupplierPartJson);
                    if (part != null && !string.IsNullOrWhiteSpace(part.SupplierPartNumber))
                        supplierPartNumber = part.SupplierPartNumber;
                    }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(supplierPartNumber))
            {
                string filter = $"SupplierPartNumber eq '{supplierPartNumber}'";
                var mappings = await _tableService.QueryEntitiesAsync<OemSupplierMapping>("OemSupplierMapping", filter);
                var mapping = mappings.FirstOrDefault();
                if (mapping != null)
                {
                    data.OemPart = new OemPart
                    {
                        OemPartNumber = mapping.OemPartNumber,
                        OemPartName = mapping.OemPartName
                    };
                }
            }
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

            // Create queue client for supplychain-warnings
            var queueClient = storageAccount.CreateCloudQueueClient();
            var queue = queueClient.GetQueueReference("supplychain-warnings");
            await queue.CreateIfNotExistsAsync();

            int queueCount = 0;
            foreach (var data in dataList)
            {
                data.PartitionKey = data.SupplierID;
                data.RowKey = Guid.NewGuid().ToString();
                data.Timestamp = DateTimeOffset.UtcNow;

                var insertOperation = TableOperation.Insert(data);
                await table.ExecuteAsync(insertOperation);

                // If status is 'delayed', send the raw SupplyChainData to queue
                if (string.Equals(data.Status, "delayed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string messageContent = System.Text.Json.JsonSerializer.Serialize(data);
                        var queueMessage = new CloudQueueMessage(messageContent);
                        await queue.AddMessageAsync(queueMessage);
                        queueCount++;
                        _logger.LogInformation($"Sent SupplyChainData to queue for supplier: {data.SupplierID}");
                    }
                    catch (Exception queueEx)
                    {
                        _logger.LogError(queueEx, $"Failed to send queue message for supplier: {data.SupplierID}");
                        // Continue processing other messages even if one fails
                    }
                }
            }
            _logger.LogInformation($"Uploaded {dataList.Count} supply chain records and sent {queueCount} queue messages (status='delayed')");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        // Return the raw SupplyChainData entities that were uploaded
        return new OkObjectResult(dataList);
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
                        SupplierPart = item.SupplierPart,
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
                        PlannedStartDate = item.PlannedStartDate,
                        PlannedCompletionDate = item.PlannedCompletionDate,
                        RippleEffect = item.RippleEffect,
                        Timestamp = item.ReportedTime
                    });
                }
                token = segment.ContinuationToken;
            } while (token != null);

            _logger.LogInformation($"Retrieved {results.Count} supply chain records.");
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

    [Function("GetSupplyChainAudioTranscriptionsBySupplierIdAndStatus")]
    public async Task<IActionResult> GetBySupplierIdAndStatus(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "supplychain/supplier/{supplierId}/status/{status}")] HttpRequest req,
    string supplierId,
    string status)
    {
        _logger.LogInformation($"Retrieving supply chain audio transcription for SupplierID: {supplierId} and Status: {status}");

        if (string.IsNullOrWhiteSpace(supplierId))
        {
            return new BadRequestObjectResult("SupplierID is required.");
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            return new BadRequestObjectResult("Status is required.");
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

            var supplierFilter = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, supplierId);
            var statusFilter = TableQuery.GenerateFilterCondition("Status", QueryComparisons.Equal, status);
            var combinedFilter = TableQuery.CombineFilters(supplierFilter, TableOperators.And, statusFilter);

            var query = new TableQuery<SupplyChainData>().Where(combinedFilter).Take(1);

            var segment = await table.ExecuteQuerySegmentedAsync(query, null);
            var result = segment.Results.FirstOrDefault();

            if (result == null)
            {
                return new NotFoundObjectResult($"No record found for SupplierID: {supplierId} and Status: {status}");
            }

            // Return as a view model for UI pop
            var viewModel = new {
                Supplier = result.SupplierID,
                Tier = result.Tier,
                Stage = result.Stage,
                SupplierPart = result.SupplierPart,
                PlannedStartDate = result.PlannedStartDate,
                PlannedCompletionDate = result.PlannedCompletionDate,
                RippleEffect = result.RippleEffect
            };

            return new OkObjectResult(viewModel);
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

        string result = @"
{
    ""name"": ""Hyundai Verna 2025"",
    ""children"": [
        {
            ""name"": ""CHSUSP-HVERNA-01"",
            ""children"": [
                {
                    ""name"": ""SUBITE-M-1718"",
                    ""children"": [
                        {
                            ""name"": ""Electronic Power"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        }
                    ]
                }
            ]
        },
        {
            ""name"": ""INTELECT-HVERNA-01"",
            ""children"": [
                {
                    ""name"": ""SUBITE-M-5410"",
                    ""children"": [
                        {
                            ""name"": ""Speakers"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        }
                    ]
                }
            ]
        },
        {
            ""name"": ""INTERIOR-HVERNA-0"",
            ""children"": [
                {
                    ""name"": ""AIRBLOWER-HVERNA-01"",
                    ""children"": [
                        {
                            ""name"": ""Air Blower Motor"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        }
                    ]
                },
                {
                    ""name"": ""DASHBOARDCONSOLE-CENTERCONSOLE-004"",
                    ""children"": [
                        {
                            ""name"": ""Center Console"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        }
                    ]
                }
            ]
        },
        {
            ""name"": ""CHASSIS_&_SUSPENSION-HV-01"",
            ""children"": [
                {
                    ""name"": ""SUSPENSION-HV-011"",
                    ""children"": [
                        {
                            ""name"": ""Subd_fan_motor"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        },
                        {
                            ""name"": ""subd_fan_motor001"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        },
                        {
                            ""name"": ""subd_fan_motor002"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        },
                        {
                            ""name"": ""subd_fan_motor003"",
                            ""children"": [
                                {
                                    ""name"": ""Neodymium""
                                }
                            ]
                        }
                    ]
                }
            ]
        }
    ]
}";

        return new OkObjectResult(result);

    }


    [Function("QueryRawMaterialGraph2")]
    public async Task<IActionResult> QueryNeo4jByRawMaterialAsync2(
[HttpTrigger(AuthorizationLevel.Function, "get", Route = "rawmaterial2/{rawMaterialName}")] HttpRequest req,
string rawMaterialName)
    {

        if (string.IsNullOrWhiteSpace(rawMaterialName))
        {
            return new BadRequestObjectResult("Missing or invalid 'rawMaterialName' in route.");
        }

        string? neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USER");
        string? neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        string? neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI");

        if (string.IsNullOrWhiteSpace(neo4jUri) || string.IsNullOrWhiteSpace(neo4jUser) || string.IsNullOrWhiteSpace(neo4jPassword))
        {
            _logger.LogError("Neo4j connection information is missing in environment variables.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }


        var driver = GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword));
        var session = driver.AsyncSession();

        var query = @"
                    MATCH (crm:ComponentRawMaterial {Name: $rawMaterialName})<- [r1:COMP_MADEOF_RAWMAT]-(c:Component)
                    <- [r2:HAS_COMPONENT]- (bsi:BomSubItem)
                    <- [r3:HAS_SUBASSEMBLY]-(bi:BomItem)
                    <- [r4:HAS_ASSEMBLY]-(mb:MaterialBOM)
                    RETURN crm, c, bsi, bi, mb, r1, r2, r3, r4
                    ";

        var parameters = new Dictionary<string, object>
        {
            { "rawMaterialName", rawMaterialName }
        };

        try
        {
            var cursor = await session.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            var testResult = System.Text.Json.JsonSerializer.Serialize(records);

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var bundles = System.Text.Json.JsonSerializer.Deserialize<List<BOMNodeBundle>>(testResult, options);

            // Debug: Log the unique Material BOMs found
            var uniqueMatBOMs = bundles.Select(b => b.mb.Properties.BillOfMaterial).Distinct().ToList();
            _logger.LogInformation($"Found Material BOMs: {string.Join(", ", uniqueMatBOMs)}");

            var groupedBOMs = bundles
                .GroupBy(b => b.mb.Properties.BillOfMaterial)
                .Select(group =>
                {
                    var mb = group.First().mb.Properties;
                    _logger.LogInformation($"Processing Material BOM: {mb.BillOfMaterial} - {mb.Material}");

                    // Group by BOM Item within each Material BOM
                    var itemGroups = group.GroupBy(g => g.bi.Properties.BillOfMaterialItem);
                    _logger.LogInformation($"  Found {itemGroups.Count()} BOM Items for {mb.BillOfMaterial}");

                    mb.ToMaterialBOMItems = itemGroups.Select(itemGroup =>
                    {
                        var bi = itemGroup.First().bi.Properties;
                        bi.ParentId = mb.BillOfMaterial;
                        _logger.LogInformation($"    Processing BOM Item: {bi.BillOfMaterialItem} - {bi.Material}");

                        // Group by BOM SubItem within each BOM Item
                        var subItemGroups = itemGroup.GroupBy(g => g.bsi.Properties.BillofMaterialSubItem);
                        _logger.LogInformation($"      Found {subItemGroups.Count()} SubItems for {bi.BillOfMaterialItem}");

                        bi.ToMaterialBOMSubItems = subItemGroups.Select(subItemGroup =>
                        {
                            var bsi = subItemGroup.First().bsi.Properties;
                            bsi.ParentId = bi.BillOfMaterialItem;
                            _logger.LogInformation($"        Processing SubItem: {bsi.BillofMaterialSubItem} - {bsi.Material}");

                            // Group by Component within each BOM SubItem
                            var componentGroups = subItemGroup.GroupBy(g => g.c.Properties.PartNumber);
                            _logger.LogInformation($"          Found {componentGroups.Count()} Components for {bsi.BillofMaterialSubItem}");

                            bsi.SubAssemblyComponents = componentGroups.Select(componentGroup =>
                            {
                                var c = componentGroup.First().c.Properties;
                                c.ParentId = bsi.BillofMaterialSubItem;

                                // Collect all raw materials for this component
                                c.RawMaterials = componentGroup.Select(g =>
                                {
                                    var crm = g.crm.Properties;
                                    crm.ParentId = c.PartNumber;
                                    return crm;
                                }).ToList();

                                return c;
                            }).ToList();

                            return bsi;
                        }).ToList();

                        return bi;
                    }).ToList();

                    return mb;
                }).ToList();


            var levels = new List<List<FlatGraphNode>>();
            var comparer = new FlatGraphNodeComparer();

            // Level 0: Material BOMs
            var level0 = groupedBOMs
                .Select(bom => new FlatGraphNode
                {
                    id = bom.BillOfMaterial,
                    displaytext = bom.Material
                })
                .Distinct(comparer)
                .ToList();
            levels.Add(level0);
            _logger.LogInformation($"Level 0 (Material BOMs): {level0.Count} items - {string.Join(", ", level0.Select(x => x.id))}");

            // Level 1: BOM Items
            var level1 = groupedBOMs
                .SelectMany(bom => bom.ToMaterialBOMItems?.Select(item => new FlatGraphNode
                {
                    id = item.BillOfMaterialItem,
                    displaytext = item.Material,
                    parents = new List<string> { bom.BillOfMaterial }
                }) ?? Enumerable.Empty<FlatGraphNode>())
                .GroupBy(node => node.id)
                .Select(group => new FlatGraphNode
                {
                    id = group.Key,
                    displaytext = group.First().displaytext,
                    parents = group.SelectMany(g => g.parents).Distinct().ToList()
                })
                .ToList();
            levels.Add(level1);
            _logger.LogInformation($"Level 1 (BOM Items): {level1.Count} items - {string.Join(", ", level1.Select(x => x.id))}");

            // Level 2: BOM SubItems
            var level2 = groupedBOMs
                .SelectMany(bom => bom.ToMaterialBOMItems?
                    .SelectMany(item => item.ToMaterialBOMSubItems?
                        .Select(sub => new FlatGraphNode
                        {
                            id = sub.BillofMaterialSubItem,
                            displaytext = sub.Material,
                            parents = new List<string> { item.BillOfMaterialItem }
                        }) ?? Enumerable.Empty<FlatGraphNode>()) ?? Enumerable.Empty<FlatGraphNode>())
                .GroupBy(node => node.id)
                .Select(group => new FlatGraphNode
                {
                    id = group.Key,
                    displaytext = group.First().displaytext,
                    parents = group.SelectMany(g => g.parents).Distinct().ToList()
                })
                .ToList();
            levels.Add(level2);
            _logger.LogInformation($"Level 2 (BOM SubItems): {level2.Count} items - {string.Join(", ", level2.Select(x => x.id))}");

            // Level 3: Components
            var level3 = groupedBOMs
                .SelectMany(bom => bom.ToMaterialBOMItems?
                    .SelectMany(item => item.ToMaterialBOMSubItems?
                        .SelectMany(sub => sub.SubAssemblyComponents?
                            .Select(comp => new FlatGraphNode
                            {
                                id = comp.PartNumber,
                                displaytext = comp.Name,
                                parents = new List<string> { sub.BillofMaterialSubItem }
                            }) ?? Enumerable.Empty<FlatGraphNode>()) ?? Enumerable.Empty<FlatGraphNode>()) ?? Enumerable.Empty<FlatGraphNode>())
                .GroupBy(node => node.id)
                .Select(group => new FlatGraphNode
                {
                    id = group.Key,
                    displaytext = group.First().displaytext,
                    parents = group.SelectMany(g => g.parents).Distinct().ToList()
                })
                .ToList();
            levels.Add(level3);
            _logger.LogInformation($"Level 3 (Components): {level3.Count} items - {string.Join(", ", level3.Select(x => x.id))}");

            // Level 4: Raw Materials
            var level4 = groupedBOMs
                .SelectMany(bom => bom.ToMaterialBOMItems?
                    .SelectMany(item => item.ToMaterialBOMSubItems?
                        .SelectMany(sub => sub.SubAssemblyComponents?
                            .SelectMany(comp => comp.RawMaterials?
                                .Select(raw => new FlatGraphNode
                                {
                                    id = $"{comp.PartNumber}::{raw.Name}",
                                    displaytext = raw.Name,
                                    parents = new List<string> { comp.PartNumber }
                                }) ?? Enumerable.Empty<FlatGraphNode>()) ?? Enumerable.Empty<FlatGraphNode>()) ?? Enumerable.Empty<FlatGraphNode>()) ?? Enumerable.Empty<FlatGraphNode>())
                .GroupBy(node => node.id)
                .Select(group => new FlatGraphNode
                {
                    id = group.Key,
                    displaytext = group.First().displaytext,
                    parents = group.SelectMany(g => g.parents).Distinct().ToList()
                })
                .ToList();
            levels.Add(level4);
            _logger.LogInformation($"Level 4 (Raw Materials): {level4.Count} items - {string.Join(", ", level4.Select(x => x.displaytext))}");


            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            };

            string json = JsonConvert.SerializeObject(levels, settings);

            return new ContentResult
            {
                Content = json,
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        catch (Exception ex)
        {
            return new StatusCodeResult(500);
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    [Function("QueryImpactedNodeCount")]
    public async Task<IActionResult> QueryRawMaterialGraphCountAsync(
[HttpTrigger(AuthorizationLevel.Function, "get", Route = "rawmaterial/count/{impactedNode}")] HttpRequest req,
string impactedNode)
    {

        if (string.IsNullOrWhiteSpace(impactedNode))
        {
            return new BadRequestObjectResult("Missing or invalid 'impactedNode' in route.");
        }

        string? neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USER");
        string? neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        string? neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI");

        if (string.IsNullOrWhiteSpace(neo4jUri) || string.IsNullOrWhiteSpace(neo4jUser) || string.IsNullOrWhiteSpace(neo4jPassword))
        {
            _logger.LogError("Neo4j connection information is missing in environment variables.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        var driver = GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword));
        var session = driver.AsyncSession();

        // To-do: Change the query to use the impactedNode parameter
        var query = @"
                    MATCH (crm:ComponentRawMaterial {Name: $rawMaterialName})<- [r1:COMP_MADEOF_RAWMAT]-(c:Component)
                    <- [r2:HAS_COMPONENT]- (bsi:BomSubItem)
                    <- [r3:HAS_SUBASSEMBLY]-(bi:BomItem)
                    <- [r4:HAS_ASSEMBLY]-(mb:MaterialBOM)
                    <- [r5: PRD_HAS_MATERIAL_BOM]-(pr:Product)
                    - [r6:PRD_IN_PRODUCTPLANT]->(pp:ProductPlant)
                    <- [r7:HAS_PRODUCTPLANT]-(p:Plant)
                    RETURN crm, c, bsi, bi, mb ,p, r1, r2, r3,r4, r7
                    ";

        var parameters = new Dictionary<string, object>
        {
            { "rawMaterialName", impactedNode }
        };

        try
        {
            var cursor = await session.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            // Count unique nodes by type
            var uniqueComponentRawMaterials = records
                .Select(r => r["crm"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueComponents = records
                .Select(r => r["c"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueBomSubItems = records
                .Select(r => r["bsi"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueBomItems = records
                .Select(r => r["bi"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueMaterialBOMs = records
                .Select(r => r["mb"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniquePlants = records
                .Select(r => r["p"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var plantNames = records
                .Select(r => r["p"].As<INode>())
                .Where(node => node.Properties.ContainsKey("Name"))
                .Select(node => node.Properties["Name"].As<string>())
                .Distinct()
                .ToList();


            var result = new ImpactedNodeCount
            {
                ComponentRawMaterialCount = uniqueComponentRawMaterials,
                ComponentCount = uniqueComponents,
                BomSubItemCount = uniqueBomSubItems,
                BomItemCount = uniqueBomItems,
                MaterialBOMCount = uniqueMaterialBOMs,
                PlantCount = uniquePlants,
                PlantNames = plantNames,
                RawMaterialName = impactedNode
            };

            _logger.LogInformation($"Found {uniqueComponentRawMaterials} ComponentRawMaterials, {uniqueComponents} Components, {uniqueBomSubItems} BomSubItems, {uniqueBomItems} BomItems, {uniqueMaterialBOMs} MaterialBOMs, {uniquePlants} Plants ({string.Join(", ", plantNames)}) for impacted node: {impactedNode}");

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while querying raw material graph count");
            return new StatusCodeResult(500);
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    [Function("GetSupplierTimeline")]
    public async Task<IActionResult> GetSupplierTimeline(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        _logger.LogInformation("Retrieving supplier timeline data.");

        var supplierTimeline = new SupplierTimelineResponse
        {
            SupplierTimeline = new SupplierTimelineData
            {
                Suppliers = new List<SupplierTimelineItem>
                {
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 4",
                        SupplierName = "T4_Magnet_Neo",
                        StartDate = "2025-03-31",
                        EndDate = "2025-05-04"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_EPS_Comp",
                        StartDate = "2025-05-05",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_Speaker_Dashboard_Comp",
                        StartDate = "2025-05-05",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_Airblower_Comp",
                        StartDate = "2025-05-05",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_Fan_Motor_Comp",
                        StartDate = "2025-05-05",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_Suspension_Subassm",
                        StartDate = "2025-06-02",
                        EndDate = "2025-06-22"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_EPS_Subassm",
                        StartDate = "2025-06-02",
                        EndDate = "2025-06-22"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_Int.Electronics_Subassm",
                        StartDate = "2025-06-02",
                        EndDate = "2025-06-22"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_Airblower_Subassm",
                        StartDate = "2025-06-02",
                        EndDate = "2025-06-22"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_Chassis_Assm",
                        StartDate = "2025-06-23",
                        EndDate = "2025-07-13"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_Suspension_Assm",
                        StartDate = "2025-06-23",
                        EndDate = "2025-07-13"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_InteriorElectronics_Assm",
                        StartDate = "2025-06-23",
                        EndDate = "2025-07-13"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_Interior2_Assm",
                        StartDate = "2025-06-23",
                        EndDate = "2025-07-13"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "OEM",
                        SupplierName = "Chennai Plant",
                        StartDate = "2025-07-14",
                        EndDate = "2025-08-03"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "OEM",
                        SupplierName = "HYD Plant",
                        StartDate = "2025-07-14",
                        EndDate = "2025-08-03"
                    }
                }
            }
        };

        return new OkObjectResult(supplierTimeline);
    }

    [Function("GetSupplyShortageDetailsForICAFlow")]
    public async Task<IActionResult> GetSupplyShortageDetailsForICAFlowAsync([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        // Extract query parameters
        string? oemProductionBatchWeek = req.Query["oemProductionBatchWeek"];
        string? impactPartNumber = req.Query["impactMaterialPartNumber"];
        string? impactPartCategory = req.Query["impactMaterialCategory"];
        string? impactPlant = req.Query["impactPlant"];

        int horizonDuration = 0;
        int.TryParse(req.Query["horizonDuration"], out horizonDuration);
        // To-Do: There isn't a well-defined logic to derive the horizon duration.
        // We will keep this as 4 for the first run and fix this in the next iteration

        if (string.IsNullOrWhiteSpace(oemProductionBatchWeek) ||
            string.IsNullOrWhiteSpace(impactPartNumber) ||
            string.IsNullOrWhiteSpace(impactPartCategory) ||
            horizonDuration <= 0)
        {
            return new BadRequestObjectResult("Missing or invalid input parameters in route or query.");
        }

        string? neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USER");
        string? neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        string? neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI");

        if (string.IsNullOrWhiteSpace(neo4jUri) || string.IsNullOrWhiteSpace(neo4jUser) || string.IsNullOrWhiteSpace(neo4jPassword))
        {
            _logger.LogError("Neo4j connection information is missing in environment variables.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        var driver = GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword));
        var session = driver.AsyncSession();

        //TO-DO : 1. This will be one big monolithic method. Refactoring has to be done

        // Step 1: Get the number of vehicles from the SupplyDemand plan for the targeted horizon duration
        // Horizon weeks for the Cypher 

        var horizonWeeks = new List<string>();
        int impactWeekNumber = int.TryParse(System.Text.RegularExpressions.Regex.Match(oemProductionBatchWeek ?? "", @"\d+").Value, out var num) ? num : 0;
        for (int counter = 0; counter < horizonDuration; counter++)
        {
            // To-Do- Remove the hardcoded year value
            horizonWeeks.Add(String.Format("2025-W{0}", impactWeekNumber + counter));
        }


        // The UI is expected to send the following based on the tier of the disruption and the details of the part number 
        // Note: The part number if not captured correctly from the voice note, the rest of the flow will error out
        // To-Do:The fallback has to be coded to check and assign the correct part number
        // To-Do: impactPartCategory = "BomItem" if Tier-1 | "BomSubItem" if Tier-2 | "Component" if Tier-3 and | "ComponentRawMaterial" if Tier-4
        // For the current flow involving neodymium magnets, the UI is expected to send "ComponentRawMaterial"

        // To-Do : Update the code to construct the query dynamically based on the nature of the part affected
        string supplyPartShortageCountQuery = @"
            MATCH (mb:MaterialBOM)
            WHERE mb.Material = $material
            OPTIONAL MATCH (mb)-[:HAS_ASSEMBLY]->(bi:BomItem)
            OPTIONAL MATCH (bi)-[:HAS_SUBASSEMBLY]->(si:BomSubItem)
            OPTIONAL MATCH (si)-[:HAS_COMPONENT]->(c:Component)
            OPTIONAL MATCH (c)-[:COMP_MADEOF_RAWMAT]->(rm:ComponentRawMaterial {Name: $rawMaterialName})
            WITH mb.BillOfMaterial AS billOfMaterial, COUNT(rm) AS totalRawMaterialCount
            RETURN totalRawMaterialCount
        ";


        var supplyShortageQueryParameters = new Dictionary<string, object>
        {
            { "rawMaterialName", impactPartNumber },           // string, e.g. "magnet-partnumber"
            { "material", "" }         //To-Do- Fill this dynamically inside the loop
        };

        var forcastQuery = @"
            MATCH (n:MRPSupplyDemand)
            WHERE n.MRPPlant = $impactPlant
              AND n.PeriodOrSegment IN $horizonWeeks
            RETURN n";

        var forecastQueryparameters = new Dictionary<string, object>
        {
            { "impactPlant", impactPlant },           // string, e.g. "CHN-PLANT-01"
            { "horizonWeeks", horizonWeeks }          // List<string>, e.g. ["2025-W28", ...]
        };


        try
        {
            var cursor = await session.RunAsync(forcastQuery, forecastQueryparameters);
            var records = await cursor.ToListAsync();


            // Pseudocode:
            // 1. Create a dictionary to group nodes by the "Material" property.
            // 2. Iterate through the records, extract the "Material" property from each node.
            // 3. Add each node to the corresponding group in the dictionary.
            // 4. (Optional) Convert the dictionary to a list or other structure as needed.

            // Implementation:
            var materialGroups = new Dictionary<string, List<INode>>();

            foreach (var record in records)
            {
                var node = record["n"].As<INode>();
                if (node.Properties.TryGetValue("Material", out var materialObj) && materialObj is string material)
                {
                    if (!materialGroups.ContainsKey(material))
                    {
                        materialGroups[material] = new List<INode>();
                    }
                    materialGroups[material].Add(node);
                }
            }

            List<SupplyShortageSummaryViewModel> supplyshortageSummaryViewColl = new List<SupplyShortageSummaryViewModel>();
            foreach (var kvp in materialGroups)
            {
                string material = kvp.Key; // this would return the MATERIAL value i.e, the Vehicle name/code 
                List<INode> supplyDemandNodes = kvp.Value; // List of all the SupplyDemand nodes from the graph

                supplyShortageQueryParameters["material"] = material; // Pass the material code to the supply shortage query
                // Code to get the number of number of pieces of the raw material in this specific vehicle type (identified by MATERIAL)

                var resultCursor = await session.RunAsync(supplyPartShortageCountQuery, supplyShortageQueryParameters);
                var queryResult = await resultCursor.ToListAsync(); // queryResult should be having only row
                int rawMaterialPerVehicle = queryResult.ElementAt(0)["totalRawMaterialCount"].As<int>();

                var shortageSummary = new SupplyShortageSummaryViewModel()
                {
                    material = material,
                    numPartsPerVehicle = rawMaterialPerVehicle,
                    weeklyForecast = new List<WeeklyForecast>()
                };

                // run the loop to instantiate the view models
                supplyDemandNodes.ForEach(node =>
                {
                    // Extract week number from "PeriodOrSegment" property (e.g., "2025-w27")
                    string periodOrSegment = node.Properties["PeriodOrSegment"]?.ToString() ?? "";
                    int weekNum = 0;
                    var match = System.Text.RegularExpressions.Regex.Match(periodOrSegment, @"\d+$");
                    if (match.Success && int.TryParse(match.Value, out int parsedWeekNum))
                    {
                        weekNum = parsedWeekNum;
                    }

                    int mrpElementOpenQuantity = Convert.ToInt32(node.Properties["MRPElementOpenQuantity"]);
                    // Add the demand forecast quantity
                    shortageSummary.weeklyForecast.Add(new WeeklyForecast
                    {
                        fgDemandQuantity = mrpElementOpenQuantity,
                        weekNum = weekNum,
                        numPartsNeeded = mrpElementOpenQuantity * rawMaterialPerVehicle

                    });

                });

                // This statement should have added the view model for the iterate vehicle type (e.g. Verna or i20)
                supplyshortageSummaryViewColl.Add(shortageSummary);

            }


            // Return the serialized view model 
            return new OkObjectResult(supplyshortageSummaryViewColl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Neo4j query in GetSupplyShortageDetailsForICAFlowAsync.");
            var errorResponse = new
            {
                error = "An error occurred while processing the request.",
                exceptionMessage = ex.Message,
                exceptionType = ex.GetType().FullName,
                stackTrace = ex.StackTrace
            };
            return new ObjectResult(errorResponse)
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
        finally
        {
            await session.CloseAsync();
        }
    }


    [Function("CreateSupplierProfile")]
    public async Task<IActionResult> CreateSupplierProfileAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        _logger.LogInformation("Creating supplier profiles.");

        List<SupplierProfileCreationRequestModel>? supplierProfileRequests;
        try
        {
            supplierProfileRequests = await System.Text.Json.JsonSerializer.DeserializeAsync<List<SupplierProfileCreationRequestModel>>(
                req.Body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize supplier profile JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (supplierProfileRequests == null || supplierProfileRequests.Count == 0)
        {
            return new BadRequestObjectResult("At least one supplier profile is required.");
        }

        var responses = new List<SupplierProfileCreationResponseModel>();
        var errors = new List<string>();

        foreach (var supplierProfileRequest in supplierProfileRequests)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(supplierProfileRequest.SupplierName))
                {
                    errors.Add($"SupplierName is required for one of the supplier profiles.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(supplierProfileRequest.PlantName))
                {
                    errors.Add($"PlantName is required for supplier: {supplierProfileRequest.SupplierName}.");
                    continue;
                }

                string supplierId = $"Supp-{Guid.NewGuid().ToString("N")[..4]}";
                string plantId = $"Plant-{Guid.NewGuid().ToString("N")[..4]}";
                var profileEntity = new SupplierProfileBase
                {
                    SupplierId = supplierId,
                    SupplierName = supplierProfileRequest.SupplierName,
                    Timestamp = DateTimeOffset.UtcNow
                };

                var supplierPlantEntity = new SupplierPlant
                {
                    SupplierId = supplierId,
                    PlantId = plantId,
                    PlantName = supplierProfileRequest.PlantName,
                    Timestamp = DateTimeOffset.UtcNow
                };

                int partCount = supplierProfileRequest.SupplierPart?.Count ?? 0;
                List<SupplierPartDetail> supplierPartDetailColl = new List<SupplierPartDetail>();
                if (partCount > 0 && supplierProfileRequest.SupplierPart != null)
                {
                        foreach (var part in supplierProfileRequest.SupplierPart)
                        {
                            var supplierPartDetails = new SupplierPartDetail
                            {
                                PartNumber = part.SupplierPartNumber,
                                PartName = part.SupplierPartName,
                                SupplierId = supplierId
                            };
                            supplierPartDetailColl.Add(supplierPartDetails);
                        }
                }

                try
                {
                    await _tableService.AddEntityAsync("SupplierProfiles", profileEntity);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error writing supplier profile to Azure Table Storage for supplier: {supplierProfileRequest.SupplierName}");
                    errors.Add($"Failed to create profile for supplier: {supplierProfileRequest.SupplierName}");
                    continue;
                }

                try
                {
                    await _tableService.AddEntityAsync("SupplierPlants", supplierPlantEntity);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error writing supplier plant details to Azure Table Storage for supplier: {supplierProfileRequest.SupplierName}");
                    errors.Add($"Failed to create plant details for supplier: {supplierProfileRequest.SupplierName}");
                    continue;
                }

                try
                {
                    await _tableService.AddEntitiesAsync("SupplierPartDetails", supplierPartDetailColl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error writing supplier part numbers to Azure Table Storage for supplier: {supplierProfileRequest.SupplierName}");
                    errors.Add($"Failed to create part details for supplier: {supplierProfileRequest.SupplierName}");
                    continue;
                }

                // Update OemSupplierMapping table with the new supplier ID based on supplier part numbers
                try
                {
                    int updatedMappings = 0;
                    if (supplierProfileRequest.SupplierPart != null && supplierProfileRequest.SupplierPart.Count > 0)
                    {
                        foreach (var part in supplierProfileRequest.SupplierPart)
                        {
                            if (!string.IsNullOrEmpty(part.SupplierPartNumber))
                            {
                                var partNumber = part.SupplierPartNumber;
                                try
                                {
                                    // Query for existing mappings with this supplier part number
                                    string filter = $"SupplierPartNumber eq '{partNumber}'";
                                    var existingMappings = await _tableService.QueryEntitiesAsync<OemSupplierMapping>("OemSupplierMapping", filter);
                                    
                                    foreach (var mapping in existingMappings)
                                    {
                                        // Update the supplier ID in the existing mapping
                                        var updatedMapping = new OemSupplierMapping
                                        {
                                            PartitionKey = mapping.PartitionKey,
                                            RowKey = supplierId, // Update with new supplier ID
                                            OemPartNumber = mapping.OemPartNumber,
                                            OemPartName = mapping.OemPartName,
                                            SupplierId = supplierId,
                                            SupplierPartNumber = mapping.SupplierPartNumber,
                                            SupplierPartName = mapping.SupplierPartName,
                                            Timestamp = DateTimeOffset.UtcNow,
                                            ETag = Azure.ETag.All // Allow overwrite
                                        };
                                        
                                        await _tableService.UpsertEntityAsync("OemSupplierMapping", updatedMapping);
                                        updatedMappings++;
                                        _logger.LogInformation($"Updated OEM mapping for part {partNumber} with supplier ID {supplierId}");
                                    }
                                }
                                catch (Exception partEx)
                                {
                                    _logger.LogWarning(partEx, $"Failed to update OEM mapping for part number {partNumber} for supplier {supplierProfileRequest.SupplierName}");
                                    // Continue processing other parts even if one fails
                                }
                            }
                        }
                    }
                    
                    _logger.LogInformation($"Updated {updatedMappings} OEM supplier mappings for supplier {supplierProfileRequest.SupplierName}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error updating OEM supplier mappings for supplier: {supplierProfileRequest.SupplierName}");
                    // Note: This is not added to errors list as it's a supplementary operation
                    // The supplier profile creation should still be considered successful
                }

                var profileCreationResponse = new SupplierProfileCreationResponseModel
                {
                    SupplierId = supplierId,
                    PlantId = plantId,
                    SupplierName = supplierProfileRequest.SupplierName
                };

                // To-Do: Code snippet to post the supplier profile creation message (with data) to Azure storage queues
                // To-Do: For the time being we can create the Supplier node from here (to be refactored sooner than later)
                // Note: The supplier might be having different lead times for different parts and the plants they supply to
                // To-Do: Pertaining to the issue mentioned in the previous point, we need to add a 3 way connection between the part#, deliveryPlant and the leadtime value
                
                // Log supplier part details for debugging
                _logger.LogInformation($"Processing supplier {supplierProfileRequest.SupplierName}: SupplierPart collection has {supplierProfileRequest.SupplierPart?.Count ?? 0} items");
                
                // Map SupplierPart property from SupplierProfileCreationRequestModel to DTO
                var validSupplierParts = supplierProfileRequest.SupplierPart?.Where(p => 
                    !string.IsNullOrWhiteSpace(p.SupplierPartNumber) && 
                    !string.IsNullOrWhiteSpace(p.SupplierPartName))
                    .ToList() ?? new List<SupplierPart>();
                
                _logger.LogInformation($"Supplier {supplierProfileRequest.SupplierName} has {validSupplierParts.Count} valid supplier parts: [{string.Join(", ", validSupplierParts.Select(p => $"{p.SupplierPartNumber}:{p.SupplierPartName}"))}]");

                await CreateSubtierSupplierGraphNodeAsync(new SubtierSupplierDTO
                {
                    LeadTimeInDays = supplierProfileRequest.LeadTimeInDays > 0 ? supplierProfileRequest.LeadTimeInDays : 20,
                    SupplierParts = validSupplierParts,
                    SupplierId = supplierId,
                    SupplierName = supplierProfileRequest.SupplierName,
                    Tier = string.IsNullOrEmpty(supplierProfileRequest.Tier) ? "Tier-N" : supplierProfileRequest.Tier
                });

                responses.Add(profileCreationResponse);
                _logger.LogInformation($"Successfully created supplier profile for: {supplierProfileRequest.SupplierName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error processing supplier profile for: {supplierProfileRequest?.SupplierName ?? "Unknown"}");
                errors.Add($"Unexpected error processing supplier: {supplierProfileRequest?.SupplierName ?? "Unknown"}");
            }
        }

        var result = new
        {
            SuccessfulProfiles = responses,
            Errors = errors,
            TotalRequested = supplierProfileRequests.Count,
            SuccessfulCount = responses.Count,
            ErrorCount = errors.Count,
            Message = $"Processed {responses.Count} out of {supplierProfileRequests.Count} supplier profiles successfully."
        };

        if (errors.Count > 0 && responses.Count == 0)
        {
            return new BadRequestObjectResult(result);
        }
        else if (errors.Count > 0)
        {
            return new ObjectResult(result) { StatusCode = 207 }; // Multi-Status
        }

        return new OkObjectResult(result);
    }

    //[Function("StoreSupplyChainWarning")]
    //public async Task StoreSupplyChainWarningAsync(
    //    [Microsoft.Azure.Functions.Worker.QueueTrigger("supplychain-warnings", Connection = "scaudiotranscriptions")] string queueMessage)
    //{
    //    _logger.LogInformation("Processing supply chain warning from queue message.");

    //    SupplyChainData? supplyChainData;
    //    try
    //    {
    //        supplyChainData = System.Text.Json.JsonSerializer.Deserialize<SupplyChainData>(queueMessage,
    //            new JsonSerializerOptions
    //            {
    //                PropertyNameCaseInsensitive = true
    //            });
    //    }
    //    catch (System.Text.Json.JsonException ex)
    //    {
    //        _logger.LogError(ex, "Invalid JSON format in queue message.");
    //        return;
    //    }

    //    if (supplyChainData == null)
    //    {
    //        _logger.LogError("Queue message could not be deserialized to SupplyChainData.");
    //        return;
    //    }

    //    try
    //    {
    //        // Store the supply chain data as a warning in Cosmos DB
    //        var documentId = await _cosmosDbService.StoreSupplyChainWarningAsync(supplyChainData);
    //        _logger.LogInformation($"Successfully stored supply chain warning with document ID: {documentId}");
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Error storing supply chain warning from queue message.");
    //    }
    //}

    [Function("StoreSupplyChainWarningDetailsQueue")]
    public async Task StoreSupplyChainWarningDetailsAsync(
        [QueueTrigger("supplychain-warnings", Connection = "scaudiotranscriptions")] string queueMessage)
    {
        _logger.LogInformation("Processing supply chain warning from queue message (queue trigger).");

        SupplyChainData? supplyChainData;
        try
        {
            supplyChainData = System.Text.Json.JsonSerializer.Deserialize<SupplyChainData>(queueMessage,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON format in queue message.");
            return;
        }

        if (supplyChainData == null)
        {
            _logger.LogError("Queue message could not be deserialized to SupplyChainData.");
            return;
        }


        try
        {            
            // Get impacted node count data (similar to QueryImpactedNodeCount)
            var impactedNodeData = await GetImpactedNodeCountAsync(supplyChainData.SupplierPart.SupplierPartName);

            // Create list to hold supply chain warnings
            var supplyChainWarnings = new List<SupplyChainWarning>();

            // Check if there are multiple plants
            if (impactedNodeData?.PlantCount > 1 && impactedNodeData?.PlantNames != null && impactedNodeData.PlantNames.Count > 1)
            {
                _logger.LogInformation($"Multiple plants detected ({impactedNodeData.PlantCount}). Creating separate warning for each plant: {string.Join(", ", impactedNodeData.PlantNames)}");
                
                // Create a separate warning for each plant
                foreach (var plantName in impactedNodeData.PlantNames)
                {
                    var combinedWarning = new SupplyChainWarning
                    {
                        Id = Guid.NewGuid().ToString(),
                        PartitionKey = supplyChainData.SupplierID, // Keep consistent with container partition key
                        Supplier = supplyChainData?.SupplierID,
                        Tier = supplyChainData?.Tier,
                        Stage = supplyChainData?.Stage,
                        SupplierPart = supplyChainData?.SupplierPart,
                        OemPart = supplyChainData?.OemPart,
                        Status = supplyChainData?.Status,
                        RippleEffect = supplyChainData?.RippleEffect,
                        PlannedStartDate = supplyChainData?.PlannedStartDate,
                        PlannedCompletionDate = supplyChainData?.PlannedCompletionDate,
                        ReportedTime = supplyChainData?.ReportedTime,
                        ImpactedNode = supplyChainData?.SupplierPart.SupplierPartName,
                        ComponentRawMaterialCount = impactedNodeData?.ComponentRawMaterialCount,
                        ComponentCount = impactedNodeData?.ComponentCount,
                        BomSubItemCount = impactedNodeData?.BomSubItemCount,
                        BomItemCount = impactedNodeData?.BomItemCount,
                        MaterialBOMCount = impactedNodeData?.MaterialBOMCount,
                        PlantCount = 1, // Each warning represents one plant
                        PlantNames = new List<string> { plantName }, // Single plant for this warning
                        PlantId = plantName // Set the specific plant ID for this warning
                    };
                    
                    supplyChainWarnings.Add(combinedWarning);
                }
                
                // Store the batch of warnings in Cosmos DB
                var documentIds = await _cosmosDbService.StoreSupplyChainWarningAsync(supplyChainWarnings);
                
                _logger.LogInformation($"Successfully stored {documentIds.Count} supply chain warnings for {impactedNodeData.PlantCount} plants. Document IDs: {string.Join(", ", documentIds)}");
            }
            else
            {
                // Single plant or no plant data - create one warning as before
                var plantName = impactedNodeData?.PlantNames?.FirstOrDefault();
                var combinedWarning = new SupplyChainWarning
                {
                    Id = Guid.NewGuid().ToString(),
                    PartitionKey = supplyChainData.SupplierID,
                    Supplier = supplyChainData?.SupplierID,
                    Tier = supplyChainData?.Tier,
                    Stage = supplyChainData?.Stage,
                    SupplierPart = supplyChainData?.SupplierPart,
                    OemPart = supplyChainData?.OemPart,
                    Status = supplyChainData?.Status,
                    RippleEffect = supplyChainData?.RippleEffect,
                    PlannedStartDate = supplyChainData?.PlannedStartDate,
                    PlannedCompletionDate = supplyChainData?.PlannedCompletionDate,
                    ReportedTime = supplyChainData?.ReportedTime,
                    ImpactedNode = supplyChainData?.SupplierPart.SupplierPartName,
                    ComponentRawMaterialCount = impactedNodeData?.ComponentRawMaterialCount,
                    ComponentCount = impactedNodeData?.ComponentCount,
                    BomSubItemCount = impactedNodeData?.BomSubItemCount,
                    BomItemCount = impactedNodeData?.BomItemCount,
                    MaterialBOMCount = impactedNodeData?.MaterialBOMCount,
                    PlantCount = impactedNodeData?.PlantCount,
                    PlantNames = impactedNodeData?.PlantNames,
                    PlantId = plantName // Set the plant ID for single plant scenarios
                };

                supplyChainWarnings.Add(combinedWarning);
                
                // Store the single warning in Cosmos DB
                var documentIds = await _cosmosDbService.StoreSupplyChainWarningAsync(supplyChainWarnings);
                
                _logger.LogInformation($"Successfully stored supply chain warning with document ID: {documentIds.FirstOrDefault()}");
            }
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing supply chain warning.");
            throw;
        }
    }

    [Function("StoreBatchSupplyChainWarnings")]
    public async Task<IActionResult> StoreBatchSupplyChainWarningsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        _logger.LogInformation("Processing batch supply chain warnings storage request.");

        List<SupplyChainWarning>? supplyChainWarnings;
        try
        {
            supplyChainWarnings = await System.Text.Json.JsonSerializer.DeserializeAsync<List<SupplyChainWarning>>(
                req.Body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize supply chain warnings JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (supplyChainWarnings == null || !supplyChainWarnings.Any())
        {
            return new BadRequestObjectResult("At least one supply chain warning is required.");
        }

        try
        {
            // Validate and enrich warnings if needed
            foreach (var warning in supplyChainWarnings)
            {
                if (string.IsNullOrEmpty(warning.Id))
                {
                    warning.Id = Guid.NewGuid().ToString();
                }

                if (string.IsNullOrEmpty(warning.PartitionKey))
                {
                    warning.PartitionKey = warning.Supplier ?? "unknown";
                }

                // Set default values if not provided
                if (warning.CreatedAt == default)
                {
                    warning.CreatedAt = DateTimeOffset.UtcNow;
                }
            }

            // Store the batch of warnings in Cosmos DB
            var documentIds = await _cosmosDbService.StoreSupplyChainWarningAsync(supplyChainWarnings);
            
            _logger.LogInformation($"Successfully stored {documentIds.Count} supply chain warnings.");

            var result = new
            {
                Success = true,
                StoredCount = documentIds.Count,
                TotalRequested = supplyChainWarnings.Count,
                DocumentIds = documentIds,
                Message = $"Successfully stored {documentIds.Count} out of {supplyChainWarnings.Count} supply chain warnings."
            };

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing batch supply chain warnings.");
            
            var errorResult = new
            {
                Success = false,
                Error = "Failed to store supply chain warnings",
                Details = ex.Message
            };

            return new ObjectResult(errorResult) { StatusCode = 500 };
        }
    }

    [Function("AcknowledgeAsRisk")]
    public async Task<IActionResult> AcknowledgeAsRiskAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        _logger.LogInformation("Processing acknowledge as risk request.");

        IncidentSummary? incidentData;
        try
        {
            incidentData = await System.Text.Json.JsonSerializer.DeserializeAsync<IncidentSummary>(
                req.Body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize incident summary JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (incidentData == null)
        {
            return new BadRequestObjectResult("Incident summary data is required.");
        }

        if (string.IsNullOrWhiteSpace(incidentData.IncidentId))
        {
            return new BadRequestObjectResult("Incident ID is required.");
        }

        try
        {
            // Create risk acknowledgment response in the specified format
            var riskAcknowledgment = new
            {
                incident_id = incidentData.IncidentId,
                detected_at = incidentData.DetectedAt,
                sector = incidentData.Sector,
                subsector = incidentData.Subsector,
                component = new
                {
                    name = incidentData.Component.Name,
                    units_per_vehicle = incidentData.Component.UnitsPerVehicle,
                    taxonomy = incidentData.Component.Taxonomy
                },
                lead_time_slip_weeks = incidentData.LeadTimeSlipWeeks,
                lead_time_slip_tolerance_weeks = incidentData.LeadTimeSlipToleranceWeeks,
                region = incidentData.Region,
                root_cause_hypothesis = incidentData.RootCauseHypothesis,
                urgency = incidentData.Urgency,
                horizon_weeks = incidentData.HorizonWeeks,
                impact_tier = incidentData.ImpactTier,
                oem_Production_BatchWeek = incidentData.OemProductionBatchWeek,
                impact_Material_Category = incidentData.ImpactMaterialCategory,
                impact_Plant = incidentData.ImpactPlant,
                free_text = incidentData.FreeText
            };

            // Log the acknowledgment for audit trail
            _logger.LogInformation($"Risk acknowledged for incident {incidentData.IncidentId}: {System.Text.Json.JsonSerializer.Serialize(riskAcknowledgment)}");

            // Store the risk acknowledgment in the database (could be extended to store in Cosmos DB)
            await StoreRiskAcknowledgmentAsync(incidentData.IncidentId, riskAcknowledgment);

            return new OkObjectResult(riskAcknowledgment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing risk acknowledgment for incident {incidentData.IncidentId}.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task StoreRiskAcknowledgmentAsync(string incidentId, object acknowledgment)
    {
        try
        {
            // Serialize the acknowledgment data for logging
            var acknowledgmentJson = System.Text.Json.JsonSerializer.Serialize(acknowledgment, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            _logger.LogInformation($"Risk acknowledgment stored for incident {incidentId}: {acknowledgmentJson}");
            
            // Store the risk acknowledgment in Cosmos DB
            await _cosmosDbService.StoreRiskAcknowledgmentAsync(incidentId, acknowledgment);
            
            _logger.LogInformation($"Successfully stored risk acknowledgment in Cosmos DB for incident {incidentId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to store risk acknowledgment for incident {incidentId}");
            throw;
        }
    }

    private async Task<SupplyChainData?> GetSupplyChainDataBySupplierIdAndStatusAsync(string supplierId, string status)
    {
        string? storageConnectionString = Environment.GetEnvironmentVariable("scaudiotranscriptions");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            _logger.LogError("connection string is null");
            return null;
        }

        try
        {
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("SCAudioTranscriptions");
            await table.CreateIfNotExistsAsync();

            var supplierFilter = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, supplierId);
            var statusFilter = TableQuery.GenerateFilterCondition("Status", QueryComparisons.Equal, status);
            var combinedFilter = TableQuery.CombineFilters(supplierFilter, TableOperators.And, statusFilter);

            var query = new TableQuery<SupplyChainData>().Where(combinedFilter).Take(1);
            var segment = await table.ExecuteQuerySegmentedAsync(query, null);
            return segment.Results.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading supply chain data from Azure Table Storage.");
            return null;
        }
    }

    private async Task<ImpactedNodeCount?> GetImpactedNodeCountAsync(string impactedNode)
    {
        string? neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USER");
        string? neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        string? neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI");

        if (string.IsNullOrWhiteSpace(neo4jUri) || string.IsNullOrWhiteSpace(neo4jUser) || string.IsNullOrWhiteSpace(neo4jPassword))
        {
            _logger.LogError("Neo4j connection information is missing in environment variables.");
            return null;
        }

        var driver = GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword));
        var session = driver.AsyncSession();

        var query = @"
            MATCH (crm:ComponentRawMaterial {Name: $rawMaterialName})<- [r1:COMP_MADEOF_RAWMAT]-(c:Component)
            <- [r2:HAS_COMPONENT]- (bsi:BomSubItem)
            <- [r3:HAS_SUBASSEMBLY]-(bi:BomItem)
            <- [r4:HAS_ASSEMBLY]-(mb:MaterialBOM)
            <- [r5: PRD_HAS_MATERIAL_BOM]-(pr:Product)
            - [r6:PRD_IN_PRODUCTPLANT]->(pp:ProductPlant)
            <- [r7:HAS_PRODUCTPLANT]-(p:Plant)
            RETURN crm, c, bsi, bi, mb ,p, r1, r2, r3,r4, r7
            ";

        var parameters = new Dictionary<string, object>
        {
            { "rawMaterialName", impactedNode }
        };

        try
        {
            var cursor = await session.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            // Count unique nodes by type
            var uniqueComponentRawMaterials = records
                .Select(r => r["crm"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueComponents = records
                .Select(r => r["c"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueBomSubItems = records
                .Select(r => r["bsi"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueBomItems = records
                .Select(r => r["bi"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniqueMaterialBOMs = records
                .Select(r => r["mb"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var uniquePlants = records
                .Select(r => r["p"].As<INode>().ElementId)
                .Distinct()
                .Count();

            var plantNames = records
                .Select(r => r["p"].As<INode>())
                .Where(node => node.Properties.ContainsKey("Plant"))
                .Select(node => node.Properties["Plant"].As<string>())
                .Distinct()
                .ToList();

            return new ImpactedNodeCount
            {
                ComponentRawMaterialCount = uniqueComponentRawMaterials,
                ComponentCount = uniqueComponents,
                BomSubItemCount = uniqueBomSubItems,
                BomItemCount = uniqueBomItems,
                MaterialBOMCount = uniqueMaterialBOMs,
                PlantCount = uniquePlants,
                PlantNames = plantNames,
                RawMaterialName = impactedNode
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while querying raw material graph count");
            return null;
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    private async Task<bool> CreateSubtierSupplierGraphNodeAsync(SubtierSupplierDTO subtierSupplier)
    {
        if (subtierSupplier?.SupplierParts == null || subtierSupplier.SupplierParts.Count == 0)
        {
            _logger.LogWarning("No supplier parts provided for supplier {SupplierId}.", subtierSupplier?.SupplierId);
            return false;
        }

        _logger.LogInformation("Creating Neo4j graph nodes for supplier {SupplierId} with {PartCount} parts",
            subtierSupplier.SupplierId, subtierSupplier.SupplierParts.Count);

        // Build supplierPart -> oemPart map from Azure Table "OemSupplierMapping"
        var oemMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplierPart in subtierSupplier.SupplierParts)
        {
            try
            {
                string filter = $"SupplierPartNumber eq '{supplierPart.SupplierPartNumber}'";
                var mappings = await _tableService.QueryEntitiesAsync<OemSupplierMapping>("OemSupplierMapping", filter);

                foreach (var mapping in mappings)
                {
                    if (!string.IsNullOrWhiteSpace(mapping.OemPartNumber))
                    {
                        oemMap[supplierPart.SupplierPartNumber] = mapping.OemPartNumber;
                        _logger.LogInformation("Found OEM mapping: {SupplierPart} -> {OemPart}", supplierPart.SupplierPartNumber, mapping.OemPartNumber);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mapping lookup failed for SupplierPart {SupplierPartNumber}", supplierPart.SupplierPartNumber);
            }
        }

        _logger.LogInformation("Found {MappingCount} OEM mappings out of {PartCount} parts", oemMap.Count, subtierSupplier.SupplierParts.Count);

        // Cypher query to create supplier and parts with enhanced part information
        var cypher = @"
    MERGE (s:SubtierSupplier { SupplierId: $supplierId })
    SET s.SupplierName = $supplierName,
        s.LeadTimeInDays = $leadTimeInDays,
        s.Tier = $supplierTier,
        s.CreatedAt = datetime(),
        s.UpdatedAt = datetime()
    
    WITH s
    UNWIND $supplierParts AS supplierPartData
    MERGE (sp:SupplierPart { PartNumber: supplierPartData.SupplierPartNumber, PartName: supplierPartData.SupplierPartName })
    SET sp.CreatedAt = CASE WHEN sp.CreatedAt IS NULL THEN datetime() ELSE sp.CreatedAt END,
        sp.UpdatedAt = datetime()
    
    WITH s, sp, supplierPartData
    MERGE (s)-[r:SUPPLIES]->(sp)
    SET r.CreatedAt = datetime()
    
    WITH s, collect(sp) as supplierParts, collect(supplierPartData.SupplierPartNumber) as partNumbers, collect(supplierPartData.SupplierPartName) as partNames
    RETURN s.SupplierId as supplierId, 
           SIZE(supplierParts) as partsCreated,
           partNumbers as createdPartNumbers,
           partNames as createdPartNames
    ";

        var parameters = new Dictionary<string, object>
    {
        { "supplierId", subtierSupplier.SupplierId },
        { "supplierName", subtierSupplier.SupplierName },
        { "leadTimeInDays", subtierSupplier.LeadTimeInDays ?? 0 },
        { "supplierParts", subtierSupplier.SupplierParts.Select(p => new Dictionary<string, object>
            {
                { "SupplierPartNumber", p.SupplierPartNumber ?? string.Empty },
                { "SupplierPartName", p.SupplierPartName ?? string.Empty }
            }).ToList() },
        { "supplierTier", subtierSupplier.Tier ?? string.Empty }
    };

        string? neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI");
        string? neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USER");
        string? neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");

        if (string.IsNullOrWhiteSpace(neo4jUri) || string.IsNullOrWhiteSpace(neo4jUser) || string.IsNullOrWhiteSpace(neo4jPassword))
        {
            _logger.LogError("Neo4j connection information is missing.");
            return false;
        }

        try
        {
            using var driver = GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword));
            await using var session = driver.AsyncSession();

            await session.ExecuteWriteAsync(async tx =>
            {
                // Create supplier and supplier parts
                var cursor = await tx.RunAsync(cypher, parameters);
                var records = await cursor.ToListAsync();

                if (records.Any())
                {
                    var record = records.First();
                    var partsCreated = record["partsCreated"].As<int>();
                    var createdPartNumbers = record["createdPartNumbers"].As<List<object>>().Select(x => x.ToString()).ToList();
                    var createdPartNames = record["createdPartNames"].As<List<object>>().Select(x => x.ToString()).ToList();
                    
                    // Create combined part info for logging
                    var partDetails = createdPartNumbers.Zip(createdPartNames, (number, name) => $"{number}:{name}").ToList();
                    
                    _logger.LogInformation("Successfully created supplier {SupplierId} with {PartsCreated} parts in Neo4j: [{PartDetails}]",
                        subtierSupplier.SupplierId, partsCreated, string.Join(", ", partDetails));
                }

                // Connect to existing nodes instead of creating OEMPart nodes
                if (oemMap.Any())
                {
                    var oemCypher = @"
                MATCH (s:SubtierSupplier { SupplierId: $supplierId })
                UNWIND keys($oemMap) AS supplierPartNumber
                MATCH (sp:SupplierPart { PartNumber: supplierPartNumber })
                WHERE (s)-[:SUPPLIES]->(sp)
                WITH s, sp, supplierPartNumber, $oemMap[supplierPartNumber] AS oemPartNumber
                WHERE oemPartNumber IS NOT NULL

                OPTIONAL MATCH (existingNode)
                WHERE (existingNode.Material = oemPartNumber OR existingNode.PartNumber = oemPartNumber)

                WITH s, sp, supplierPartNumber, oemPartNumber, collect(existingNode) as matchingNodes
                WHERE SIZE(matchingNodes) > 0

                UNWIND matchingNodes as matchedNode
                WITH s, sp, supplierPartNumber, oemPartNumber, matchedNode
                WHERE matchedNode IS NOT NULL
                MERGE (sp)-[:EQUIVALENT_TO { CreatedAt: datetime(), OemPartNumber: oemPartNumber }]->(matchedNode)
                MERGE (s)-[:SUPPLIES_OEM_PART { via: supplierPartNumber, CreatedAt: datetime(), OemPartNumber: oemPartNumber }]->(matchedNode)

                RETURN COUNT(DISTINCT matchedNode) as oemPartsLinked, 
                       collect(DISTINCT labels(matchedNode)) as linkedNodeTypes,
                       collect(DISTINCT coalesce(matchedNode.Material, matchedNode.PartNumber, matchedNode.Name)) as linkedNodeNames
                ";

                    var oemParameters = new Dictionary<string, object>
                {
                    { "supplierId", subtierSupplier.SupplierId },
                    { "oemMap", oemMap }
                };

                    var oemCursor = await tx.RunAsync(oemCypher, oemParameters);
                    var oemRecords = await oemCursor.ToListAsync();

                    if (oemRecords.Any())
                    {
                        var record = oemRecords.First();
                        var oemLinked = record["oemPartsLinked"].As<int>();
                        var linkedNodeTypes = record["linkedNodeTypes"].As<List<object>>()
                            .Select(x => string.Join(":", ((List<object>)x).Select(l => l.ToString())))
                            .ToList();
                        var linkedNodeNames = record["linkedNodeNames"].As<List<object>>()
                            .Select(x => x?.ToString() ?? "Unknown")
                            .ToList();

                        _logger.LogInformation("Successfully linked {OemLinked} existing nodes for supplier {SupplierId}. Node types: [{NodeTypes}], Names: [{NodeNames}]",
                            oemLinked, subtierSupplier.SupplierId, string.Join(", ", linkedNodeTypes), string.Join(", ", linkedNodeNames));

                        if (oemLinked == 0)
                        {
                            _logger.LogWarning("No existing nodes found to link for supplier {SupplierId}. OEM part numbers searched: [{OemPartNumbers}]",
                                subtierSupplier.SupplierId, string.Join(", ", oemMap.Values));
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No records returned from OEM linking query for supplier {SupplierId}", subtierSupplier.SupplierId);
                    }
                }
                else
                {
                    _logger.LogInformation("No OEM mappings found for supplier {SupplierId}", subtierSupplier.SupplierId);
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create supplier node and relationships in Neo4j for supplier {SupplierId}",
                subtierSupplier.SupplierId);
            return false;
        }
    }

    private async Task<string?> GetOemPartNumberForSupplierPartAsync(string supplierPart)
    {
        try
        {

            string? neo4jUser = Environment.GetEnvironmentVariable("NEO4J_USER");
            string? neo4jPassword = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
            string? neo4jUri = Environment.GetEnvironmentVariable("NEO4J_URI");


            // Example: read from Azure Table Storage (OemSupplierMapping)
            var entity = await _tableService.GetEntityAsync<OemSupplierMapping>("OemSupplierMapping", "Mapping", supplierPart);
            return entity?.OemPartNumber;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OEM mapping lookup failed for SupplierPart {supplierPart}");
            return null;
        }
    }


    [Function("SupplierMinimalSignIn")]
    public async Task<IActionResult> SupplierMinimalSignInAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        // Read sign-in values from POST request body instead of query parameters
        SupplierMinimalSigninModel? signInRequest;
        try
        {
            signInRequest = await System.Text.Json.JsonSerializer.DeserializeAsync<SupplierMinimalSigninModel>(
                req.Body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize sign-in request JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (signInRequest == null ||
            string.IsNullOrWhiteSpace(signInRequest.SupplierId) ||
            string.IsNullOrWhiteSpace(signInRequest.SupplierName) ||
            string.IsNullOrWhiteSpace(signInRequest.PlantId))
        {
            return new BadRequestObjectResult("supplierId, supplierName and plantId are required.");
        }

        string supplierId = signInRequest.SupplierId;
        string supplierName = signInRequest.SupplierName;
        string plantId = signInRequest.PlantId;

        // Attempt to retrieve the supplier profile from Azure Table Storage
        var supplierProfile = await _tableService.GetEntityAsync<SupplierProfileBase>("SupplierProfiles", supplierId, supplierName);

        if (supplierProfile == null)
        {
            // Supplier not found
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Supplier not found. Please check your Supplier ID."
            });
        }

        // Validate supplier name (case-insensitive)
        if (!string.Equals(supplierProfile.SupplierName, supplierName, StringComparison.OrdinalIgnoreCase))
        {
            // Supplier name does not match
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Supplier name does not match the record."
            });
        }

        // Future enhancement: Add audit logging for sign-in attempts

        // Successful sign-in
        return new OkObjectResult(new
        {
            success = true,
            supplierId = supplierProfile.SupplierId,
            supplierName = supplierProfile.SupplierName,
            message = "Sign-in successful."
        });

    }


    [Function("CreateProductionBatch")]
    public async Task<IActionResult> CreateProductionBatchAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        // Read and deserialize the POST request body to SupplierBatchCreationModel
        SupplierBatchCreationModel? batchRequest;
        try
        {
            batchRequest = await System.Text.Json.JsonSerializer.DeserializeAsync<SupplierBatchCreationModel>(
                req.Body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize production batch request JSON.");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        if (batchRequest == null)
        {
            return new BadRequestObjectResult("Production batch request body is required.");
        }

        string batchId = $"Batch-{Guid.NewGuid().ToString("N")[..4]}";
        var batchData = new SupplierBatchDetails
        {
            SupplierId = batchRequest.SupplierId,
            PlantId = batchRequest.PlantId,
            BatchId = batchId,
            BatchStartDate = batchRequest.BatchStartDate,
            BatchEndDate = batchRequest.BatchEndDate,
            BatchStatus = SupplierBatchStatus.ACTIVE,
            SupplierBatchNumber = batchRequest.SupplierBatchNumber,
            OEMScheduleNumber = string.IsNullOrEmpty(batchRequest.OEMScheduleNumber) ? string.Empty : batchRequest.OEMScheduleNumber
        };

        try
        {
            await _tableService.AddEntityAsync("SupplierBatchDetails", batchData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing supplier batch transaction data to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        return new OkObjectResult(new { batchId, message = "Production batch request processed.", batchRequest });
    }


    [Function("GetProductionBatches")]
    public async Task<IActionResult> GetSupplierBatchesAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        // Read supplierId from query parameters
        string? supplierId = req.Query["supplierId"];
        if (string.IsNullOrWhiteSpace(supplierId))
        {
            return new BadRequestObjectResult("supplierId is required.");
        }

        // Query SupplierBatchDetails table for all batches for the supplier
        string filter = $"PartitionKey eq '{supplierId}'";
        List<SupplierBatchDetails> batchEntities;
        try
        {
            batchEntities = await _tableService.QueryEntitiesAsync<SupplierBatchDetails>("SupplierBatchDetails", filter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying SupplierBatchDetails from Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        // Map entities to response model

        List<string> batchNumbers = batchEntities.Select(b => b.SupplierBatchNumber).ToList();
        var batchQueryResponse = new SupplierBatchDetailsResponseModel
        {
            SupplierId = supplierId,
            BatchNumbers = batchNumbers
        };

        return new OkObjectResult(batchQueryResponse);
    }


    [Function("ImportLocalOemSupplierMappingCsv")]
    public async Task<IActionResult> ImportLocalOemSupplierMappingCsvAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        _logger.LogInformation("Starting import of local OEM_Supplier_Mapping.csv file.");

        string? storageConnectionString = "DefaultEndpointsProtocol=https;AccountName=scaudiotranscriptions;AccountKey=eGqgdfFK+04X2UG4Csk6f7DE4oHveQvkcHeatQD7o5L/P1UzHVz7y2g2ENrOwkq+LFQgZRuJyqk8+AStGrEDzQ==;EndpointSuffix=core.windows.net";
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            _logger.LogError("Storage connection string is null");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            // Path to the CSV file in the solution folder
            // First try to find the CSV file in the solution root directory
            string currentDirectory = Directory.GetCurrentDirectory();
            string csvFilePath = Path.Combine(currentDirectory, "OEM_Supplier_Mapping.csv");
            
            // If not found in current directory, try going up to find the solution folder
            if (!File.Exists(csvFilePath))
            {
                // Look for the solution file to identify the solution root
                string? solutionRoot = FindSolutionRoot(currentDirectory);
                if (!string.IsNullOrEmpty(solutionRoot))
                {
                    csvFilePath = Path.Combine(solutionRoot, "OEM_Supplier_Mapping.csv");
                }
            }
            
            // Also try common deployment paths
            if (!File.Exists(csvFilePath))
            {
                var alternativePaths = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OEM_Supplier_Mapping.csv"),
                    Path.Combine(Environment.CurrentDirectory, "OEM_Supplier_Mapping.csv"),
                    Path.Combine(Directory.GetParent(currentDirectory)?.FullName ?? currentDirectory, "OEM_Supplier_Mapping.csv"),
                    Path.Combine(Directory.GetParent(Directory.GetParent(currentDirectory)?.FullName ?? currentDirectory)?.FullName ?? currentDirectory, "OEM_Supplier_Mapping.csv")
                };
                
                foreach (var altPath in alternativePaths)
                {
                    if (File.Exists(altPath))
                    {
                        csvFilePath = altPath;
                        break;
                    }
                }
            }
            
            _logger.LogInformation($"Attempting to read CSV file from: {csvFilePath}");
            
            if (!File.Exists(csvFilePath))
            {
                _logger.LogError($"CSV file not found at path: {csvFilePath}");
                _logger.LogInformation($"Current directory: {currentDirectory}");
                _logger.LogInformation($"Base directory: {AppDomain.CurrentDomain.BaseDirectory}");
                
                // List files in current directory for debugging
                var filesInCurrentDir = Directory.GetFiles(currentDirectory, "*.csv", SearchOption.TopDirectoryOnly);
                _logger.LogInformation($"CSV files in current directory: {string.Join(", ", filesInCurrentDir)}");
                
                return new NotFoundObjectResult($"OEM_Supplier_Mapping.csv file not found. Searched in: {csvFilePath}. Current directory: {currentDirectory}");
            }

            var mappingEntities = new List<OemSupplierMapping>();
            
            using var reader = new StreamReader(csvFilePath);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            csv.Context.RegisterClassMap<OemSupplierMappingMap>();

            var records = csv.GetRecords<OemSupplierMapping>();
            foreach (var record in records)
            {
                record.Timestamp = DateTimeOffset.UtcNow;
                mappingEntities.Add(record);
                _logger.LogInformation($"Parsed mapping: OEM Part {record.OemPartNumber} -> Supplier {record.SupplierId} ({record.SupplierPartNumber})");
            }

            _logger.LogInformation($"Parsed {mappingEntities.Count} OEM Supplier Mapping records from local CSV file.");

            // Insert records into Azure Table Storage
            int insertedCount = 0;
            var errors = new List<string>();

            foreach (var mapping in mappingEntities)
            {
                try
                {
                    await _tableService.AddEntityAsync("OemSupplierMapping", mapping);
                    insertedCount++;
                    _logger.LogInformation($"Successfully inserted mapping: {mapping.OemPartNumber} -> {mapping.SupplierId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to insert mapping for OEM Part: {mapping.OemPartNumber}, Supplier: {mapping.SupplierId}");
                    errors.Add($"Failed to insert mapping for OEM Part: {mapping.OemPartNumber}, Supplier: {mapping.SupplierId} - {ex.Message}");
                }
            }

            var result = new
            {
                SourceFile = "OEM_Supplier_Mapping.csv",
                TotalRecords = mappingEntities.Count,
                SuccessfulInserts = insertedCount,
                FailedInserts = errors.Count,
                Errors = errors,
                Message = $"Imported {insertedCount} out of {mappingEntities.Count} OEM Supplier Mapping records successfully from local CSV file.",
                Details = mappingEntities.Select(m => new
                {
                    OemPartNumber = m.OemPartNumber,
                    OemPartName = m.OemPartName,
                    SupplierId = m.SupplierId,
                    SupplierPartNumber = m.SupplierPartNumber,
                    SupplierPartName = m.SupplierPartName
                }).ToList()
            };

            if (errors.Count > 0 && insertedCount == 0)
            {
                return new BadRequestObjectResult(result);
            }
            else if (errors.Count > 0)
            {
                return new ObjectResult(result) { StatusCode = 207 }; // Multi-Status
            }

            _logger.LogInformation($"Successfully imported {insertedCount} OEM Supplier Mapping records from local CSV file.");
            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing local OEM_Supplier_Mapping.csv file.");
            var errorResult = new
            {
                Error = "Failed to import OEM Supplier Mapping from local CSV file",
                ExceptionMessage = ex.Message,
                ExceptionType = ex.GetType().FullName
            };
            return new ObjectResult(errorResult) { StatusCode = StatusCodes.Status500InternalServerError };
        }
    }


    [Function("ImportOemSupplierMappingFromCsv")]
    public async Task<IActionResult> ImportOemSupplierMappingFromCsvAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        _logger.LogInformation("Starting OEM Supplier Mapping import from CSV.");

        if (!req.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            return new BadRequestObjectResult("Content-Type must be multipart/form-data for file upload.");
        }

        var form = await req.ReadFormAsync();
        var file = form.Files.GetFile("csvFile");

        if (file == null || file.Length == 0)
        {
            return new BadRequestObjectResult("CSV file is required. Please upload a file with the name 'csvFile'.");
        }

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return new BadRequestObjectResult("Only CSV files are supported.");
        }

        string? storageConnectionString = Environment.GetEnvironmentVariable("scaudiotranscriptions");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            _logger.LogError("Storage connection string is null");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var mappingEntities = new List<OemSupplierMapping>();
            
            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            csv.Context.RegisterClassMap<OemSupplierMappingMap>();

            var records = csv.GetRecords<OemSupplierMapping>();
            foreach (var record in records)
            {
                record.Timestamp = DateTimeOffset.UtcNow;
                mappingEntities.Add(record);
            }

            _logger.LogInformation($"Parsed {mappingEntities.Count} OEM Supplier Mapping records from CSV.");

            // Insert records into Azure Table Storage
            int insertedCount = 0;
            var errors = new List<string>();

            foreach (var mapping in mappingEntities)
            {
                try
                {
                    await _tableService.AddEntityAsync("OemSupplierMapping", mapping);
                    insertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to insert mapping for OEM Part: {mapping.OemPartNumber}, Supplier: {mapping.SupplierId}");
                    errors.Add($"Failed to insert mapping for OEM Part: {mapping.OemPartNumber}, Supplier: {mapping.SupplierId} - {ex.Message}");
                }
            }

            var result = new
            {
                TotalRecords = mappingEntities.Count,
                SuccessfulInserts = insertedCount,
                FailedInserts = errors.Count,
                Errors = errors,
                Message = $"Imported {insertedCount} out of {mappingEntities.Count} OEM Supplier Mapping records successfully."
            };

            if (errors.Count > 0 && insertedCount == 0)
            {
                return new BadRequestObjectResult(result);
            }
            else if (errors.Count > 0)
            {
                return new ObjectResult(result) { StatusCode = 207 }; // Multi-Status
            }

            _logger.LogInformation($"Successfully imported {insertedCount} OEM Supplier Mapping records.");
            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing OEM Supplier Mapping from CSV.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }


    [Function("ImportOemSupplierMappingFromBlob")]
    public async Task<IActionResult> ImportOemSupplierMappingFromBlobAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        _logger.LogInformation("Starting OEM Supplier Mapping import from blob storage.");

        string? storageConnectionString = Environment.GetEnvironmentVariable("scaudiotranscriptions");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            _logger.LogError("Storage connection string is null");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference("oemsuppliermapping");
            
            if (!await container.ExistsAsync())
            {
                _logger.LogError("OEM Supplier Mapping blob container does not exist.");
                return new NotFoundObjectResult("OEM Supplier Mapping blob container not found.");
            }

            BlobContinuationToken? continuationToken = null;
            int totalRecords = 0;
            var errors = new List<string>();

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
                        csv.Context.RegisterClassMap<OemSupplierMappingMap>();

                        var records = csv.GetRecords<OemSupplierMapping>();
                        foreach (var mapping in records)
                        {
                            try
                            {
                                mapping.Timestamp = DateTimeOffset.UtcNow;
                                await _tableService.AddEntityAsync("OemSupplierMapping", mapping);
                                totalRecords++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to insert mapping for OEM Part: {mapping.OemPartNumber}, Supplier: {mapping.SupplierId}");
                                errors.Add($"Failed to insert mapping for OEM Part: {mapping.OemPartNumber}, Supplier: {mapping.SupplierId}");
                            }
                        }
                    }
                }
                continuationToken = resultSegment.ContinuationToken;
            } while (continuationToken != null);

            var result = new
            {
                TotalRecordsProcessed = totalRecords,
                ErrorCount = errors.Count,
                Errors = errors,
                Message = $"OEM Supplier Mapping import complete. Total records processed: {totalRecords}, Errors: {errors.Count}"
            };

            _logger.LogInformation($"Import complete. Total records inserted: {totalRecords}, Errors: {errors.Count}");
            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing OEM Supplier Mapping from blob storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }


    [Function("GetOemSupplierMappings")]
    public async Task<IActionResult> GetOemSupplierMappingsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        _logger.LogInformation("Retrieving OEM Supplier Mappings.");

        // Optional query parameters for filtering
        string? oemPartNumber = req.Query["oemPartNumber"];
        string? supplierId = req.Query["supplierId"];

        try
        {
            string filter = string.Empty;
            
            if (!string.IsNullOrWhiteSpace(oemPartNumber) && !string.IsNullOrWhiteSpace(supplierId))
            {
                filter = $"PartitionKey eq '{oemPartNumber}' and RowKey eq '{supplierId}'";
            }
            else if (!string.IsNullOrWhiteSpace(oemPartNumber))
            {
                filter = $"PartitionKey eq '{oemPartNumber}'";
            }
            else if (!string.IsNullOrWhiteSpace(supplierId))
            {
                filter = $"RowKey eq '{supplierId}'";
            }

            List<OemSupplierMapping> mappings;
            if (string.IsNullOrWhiteSpace(filter))
            {
                mappings = await _tableService.QueryEntitiesAsync<OemSupplierMapping>("OemSupplierMapping", string.Empty);
            }
            else
            {
                mappings = await _tableService.QueryEntitiesAsync<OemSupplierMapping>("OemSupplierMapping", filter);
            }

            return new OkObjectResult(new
            {
                Count = mappings.Count,
                Mappings = mappings
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving OEM Supplier Mappings from Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    // Helper method to find the solution root directory
    private string? FindSolutionRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        
        while (directory != null)
        {
            // Look for .sln files in the current directory
            var solutionFiles = directory.GetFiles("*.sln", SearchOption.TopDirectoryOnly);
            if (solutionFiles.Length > 0)
            {
                _logger.LogInformation($"Found solution file: {solutionFiles[0].FullName}");
                return directory.FullName;
            }
            
            // Also look for the CSV file directly
            var csvFiles = directory.GetFiles("OEM_Supplier_Mapping.csv", SearchOption.TopDirectoryOnly);
            if (csvFiles.Length > 0)
            {
                _logger.LogInformation($"Found CSV file in directory: {directory.FullName}");
                return directory.FullName;
            }
            
            directory = directory.Parent;
        }
        
        return null;
    }


    [Function("CalculateSupplyChainRisk")]
    public async Task<IActionResult> CalculateSupplyChainRisk(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req)
    {
        _logger.LogInformation("Risk calculation request received.");

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        RiskCalculationRequest request;

        try
        {
            request = JsonConvert.DeserializeObject<RiskCalculationRequest>(requestBody);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Invalid JSON input: {ex.Message}");
            return new BadRequestObjectResult("Invalid JSON format.");
        }

        // Use Neo4jService to get demand quantity and average selling price
        var neo4jService = new Services.Neo4jService();
        int demandQuantity = 0;
        float averageSellingPrice = 0;
        try
        {
            // To do:
            // These values have to be parameterized as needed
            string material = "HYUNDAI-VERNA-2025";
            string plant = "CHN-PLANT-01";
            string periodStart = "2025-W27";
            string periodEnd = "2025-W30";
            string supplierContains = "OEM";
            demandQuantity = await neo4jService.GetDemandQuantityAsync(material, plant, periodStart, periodEnd);
            averageSellingPrice = await neo4jService.GetAverageSellingPriceAsync(material, supplierContains);

            if (request.SolutionCoverageQuantity < 0)
                throw new ArgumentException("Coverage quantity must be non-negative.");
            if (averageSellingPrice < 0)
                throw new ArgumentException("Average selling price cannot be negative.");
            if (request.StockoutPenaltyRate < 0)
                throw new ArgumentException("Stockout penalty rate cannot be negative.");

            int unitsAtRisk = demandQuantity - request.SolutionCoverageQuantity;
            float revenueAtRisk = unitsAtRisk * averageSellingPrice;
            float stockoutPenalty = unitsAtRisk * request.StockoutPenaltyRate;

            //To do:
            // Handle currency conversion
            // For now, Converting INR to USD (example rate: 1 USD = 83 INR) to match with ICA response.
            const float inrToUsd = 83.0f;
            float revenueAtRiskUSD = revenueAtRisk / inrToUsd;
            float stockoutPenaltyUSD = stockoutPenalty / inrToUsd;

            // Convert revenueAtRiskUSD to millions
            float revenueAtRiskUSD_Million = revenueAtRiskUSD / 1_000_000f;

            var response = new RiskCalculationResponse
            {
                UnitsAtRisk = unitsAtRisk,
                RevenueAtRisk = revenueAtRiskUSD_Million,
                StockoutPenalty = stockoutPenaltyUSD
            };

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error during calculation: {ex.Message}");
            return new BadRequestObjectResult($"Error: {ex.Message}");
        }
    }


    [Function("GetSubtierSupplierWarnings")]
    public async Task<IActionResult> GetSubtierSupplierWarnings(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "supplychain/subtier-warnings")] HttpRequest req)
    {
        _logger.LogInformation("Retrieving subtier supplier warnings from Cosmos DB.");

        // Cosmos DB connection details
        string? cosmosDbConnectionString = Environment.GetEnvironmentVariable("CosmosDbConnectionString");
        string databaseId = "ecc_alerts";
        string containerId = "supplyChainWarnings";

        if (string.IsNullOrWhiteSpace(cosmosDbConnectionString))
        {
            _logger.LogError("Cosmos DB connection string is missing.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        // Get supplierId from query string
        string? supplierId = req.Query["supplierId"];

        try
        {
            var cosmosClient = new Microsoft.Azure.Cosmos.CosmosClient(cosmosDbConnectionString);
            var container = cosmosClient.GetContainer(databaseId, containerId);

            Microsoft.Azure.Cosmos.QueryDefinition query;
            if (!string.IsNullOrWhiteSpace(supplierId))
            {
                query = new Microsoft.Azure.Cosmos.QueryDefinition("SELECT * FROM c WHERE c.DocumentType = @docType AND (c.SupplierId = @supplierId OR c.PartitionKey = @supplierId)")
                    .WithParameter("@docType", "SupplyChainWarning")
                    .WithParameter("@supplierId", supplierId);
            }
            else
            {
                query = new Microsoft.Azure.Cosmos.QueryDefinition("SELECT * FROM c WHERE c.DocumentType = @docType")
                    .WithParameter("@docType", "SupplyChainWarning");
            }

            var iterator = container.GetItemQueryIterator<dynamic>(query);
            var results = new List<object>();

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var item in response)
                {
                    // Map to view model
                    results.Add(new
                    {
                        supplier = (string?)item.SupplierId ?? (string?)item.PartitionKey,
                        tier = (string?)item.Tier,
                        stage = (string?)item.Stage,
                        material = item.SupplierPart != null && item.SupplierPart.SupplierPartName != null ? (string?)item.SupplierPart.SupplierPartName : null,
                        rippleEffect = (string?)item.RippleEffect,
                        componentRawMaterialCount = item.ComponentRawMaterialCount != null ? (int?)item.ComponentRawMaterialCount : 0,
                        componentCount = item.ComponentCount != null ? (int?)item.ComponentCount : 0,
                        bomSubItemCount = item.BomSubItemCount != null ? (int?)item.BomSubItemCount : 0,
                        bomItemCount = item.BomItemCount != null ? (int?)item.BomItemCount : 0,
                        materialBOMCount = item.MaterialBOMCount != null ? (int?)item.MaterialBOMCount : 0,
                        plantCount = item.PlantCount != null ? (int?)item.PlantCount : 0,
                        plantNames = item.PlantNames != null ? item.PlantNames : new List<string>(),
                        rawMaterialName = ((item.SupplierPart != null && item.SupplierPart.SupplierPartName != null) ? ((string)item.SupplierPart.SupplierPartName).Replace(" ", "-").ToLowerInvariant() : null)
                    });
                }
            }

            return new OkObjectResult(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Cosmos DB for subtier supplier warnings.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }


}




/*
#region CODE_PARTCOUNT_PER_MATERIAL_CATEGORY
// To-Do: To be integrated into the main method. 
/* string query;
 string partParamName;
 string matchClause;

 switch (impactPartCategory)
 {
     case "BomItem":
         matchClause = "OPTIONAL MATCH (mb)-[:HAS_ASSEMBLY]->(bi:BomItem {BillOfMaterialItem: $partNumber})";
         partParamName = "partNumber";
         query = @"
             MATCH (mb:MaterialBOM)
             WHERE mb.Material = $material
             " + matchClause + @"
             WITH mb.BillOfMaterial AS billOfMaterial, COUNT(bi) AS totalPartCount
             RETURN totalPartCount
         ";
         break;
     case "BomSubItem":
         matchClause = @"
             OPTIONAL MATCH (mb)-[:HAS_ASSEMBLY]->(bi:BomItem)
             OPTIONAL MATCH (bi)-[:HAS_SUBASSEMBLY]->(si:BomSubItem {BillofMaterialSubItem: $partNumber})
         ";
         partParamName = "partNumber";
         query = @"
             MATCH (mb:MaterialBOM)
             WHERE mb.Material = $material
             " + matchClause + @"
             WITH mb.BillOfMaterial AS billOfMaterial, COUNT(si) AS totalPartCount
             RETURN totalPartCount
         ";
         break;
     case "Component":
         matchClause = @"
             OPTIONAL MATCH (mb)-[:HAS_ASSEMBLY]->(bi:BomItem)
             OPTIONAL MATCH (bi)-[:HAS_SUBASSEMBLY]->(si:BomSubItem)
             OPTIONAL MATCH (si)-[:HAS_COMPONENT]->(c:Component {PartNumber: $partNumber})
         ";
         partParamName = "partNumber";
         query = @"
             MATCH (mb:MaterialBOM)
             WHERE mb.Material = $material
             " + matchClause + @"
             WITH mb.BillOfMaterial AS billOfMaterial, COUNT(c) AS totalPartCount
             RETURN totalPartCount
         ";
         break;
     case "ComponentRawMaterial":
         matchClause = @"
             OPTIONAL MATCH (mb)-[:HAS_ASSEMBLY]->(bi:BomItem)
             OPTIONAL MATCH (bi)-[:HAS_SUBASSEMBLY]->(si:BomSubItem)
             OPTIONAL MATCH (si)-[:HAS_COMPONENT]->(c:Component)
             OPTIONAL MATCH (c)-[:COMP_MADEOF_RAWMAT]->(rm:ComponentRawMaterial {Name: $partNumber})
         ";
         partParamName = "partNumber";
         query = @"
             MATCH (mb:MaterialBOM)
             WHERE mb.Material = $material
             " + matchClause + @"
             WITH mb.BillOfMaterial AS billOfMaterial, COUNT(rm) AS totalPartCount
             RETURN totalPartCount
         ";
         break;
     default:
         throw new ArgumentException("Invalid impactPartCategory");
 }

 // Usage example:
 var parameters = new Dictionary<string, object>
 {
     { "material", material },
     { partParamName, impactPartNumber }
 };
#endregion
    // Usage example:
    var parameters = new Dictionary<string, object>
    {
        { "material", material },
        { partParamName, impactPartNumber }
    };
   */

