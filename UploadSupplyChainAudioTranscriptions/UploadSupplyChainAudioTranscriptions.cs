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
using System.Text;
using System.Globalization;
using UploadSupplyChainAudioTranscriptions.Entities;
using UploadSupplyChainAudioTranscriptions.Services;
using Newtonsoft.Json;



namespace UploadSupplyChainAudioTranscriptions;

public class UploadSupplyChainAudioTranscriptions
{
    private readonly ILogger<UploadSupplyChainAudioTranscriptions> _logger;
    private readonly AzureTableService _tableService;

    public UploadSupplyChainAudioTranscriptions(
        ILogger<UploadSupplyChainAudioTranscriptions> logger,
        AzureTableService tableService)
    {
        _logger = logger;
        _tableService = tableService;
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
            dataList = await System.Text.Json.JsonSerializer.DeserializeAsync<List<SupplyChainData>>(req.Body,
            new System.Text.Json.JsonSerializerOptions
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

            return new OkObjectResult(result);
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
                    MATCH (crm:ComponentRawMaterial {Name: 'Neodymium Magnet'})<- [r1:COMP_MADEOF_RAWMAT]-(c:Component)
                    <- [r2:HAS_COMPONENT]- (bsi:BomSubItem)
                    <- [r3:HAS_SUBASSEMBLY]-(bi:BomItem)
                    <- [r4:HAS_ASSEMBLY]-(mb:MaterialBOM)
                    RETURN crm, c, bsi, bi, mb, r1, r2, r3, r4
                    ";


        try
        {
            var cursor = await session.RunAsync(query);
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

        var query = @"
                    MATCH (crm:ComponentRawMaterial {Name: 'Neodymium Magnet'})<- [r1:COMP_MADEOF_RAWMAT]-(c:Component)
                    <- [r2:HAS_COMPONENT]- (bsi:BomSubItem)
                    <- [r3:HAS_SUBASSEMBLY]-(bi:BomItem)
                    <- [r4:HAS_ASSEMBLY]-(mb:MaterialBOM)
                    RETURN crm, c, bsi, bi, mb, r1, r2, r3, r4
                    ";

        try
        {
            var cursor = await session.RunAsync(query);
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


            var result = new ImpactedNodeCount
            {
                ComponentRawMaterialCount = uniqueComponentRawMaterials,
                ComponentCount = uniqueComponents,
                BomSubItemCount = uniqueBomSubItems,
                BomItemCount = uniqueBomItems,
                MaterialBOMCount = uniqueMaterialBOMs,
                RawMaterialName = impactedNode
            };

            _logger.LogInformation($"Found {uniqueComponentRawMaterials} ComponentRawMaterials, {uniqueComponents} Components, {uniqueBomSubItems} BomSubItems, {uniqueBomItems} BomItems, {uniqueMaterialBOMs} MaterialBOMs for impacted node: {impactedNode}");

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
                        EndDate = "2025-04-27"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_EPS_Comp",
                        StartDate = "2025-04-28",
                        EndDate = "2025-05-11"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_Speaker_Dashboard_Comp",
                        StartDate = "2025-04-28",
                        EndDate = "2025-05-11"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_Airblower_Comp",
                        StartDate = "2025-04-28",
                        EndDate = "2025-05-11"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 3",
                        SupplierName = "T3_Fan_Motor_Comp",
                        StartDate = "2025-04-28",
                        EndDate = "2025-05-11"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_Suspension_Subassm",
                        StartDate = "2025-05-12",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_EPS_Subassm",
                        StartDate = "2025-05-12",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_Int.Electronics_Subassm",
                        StartDate = "2025-05-12",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 2",
                        SupplierName = "T2_Airblower_Subassm",
                        StartDate = "2025-05-12",
                        EndDate = "2025-05-25"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_Chassis_Assm",
                        StartDate = "2025-05-26",
                        EndDate = "2025-06-08"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_Suspension_Assm",
                        StartDate = "2025-05-26",
                        EndDate = "2025-06-08"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_InteriorElectronics_Assm",
                        StartDate = "2025-05-26",
                        EndDate = "2025-06-08"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "Tier 1",
                        SupplierName = "T1_Interior2_Assm",
                        StartDate = "2025-05-26",
                        EndDate = "2025-06-08"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "OEM",
                        SupplierName = "Chennai Plant",
                        StartDate = "2025-06-09",
                        EndDate = "2025-06-22"
                    },
                    new SupplierTimelineItem
                    {
                        Tier = "OEM",
                        SupplierName = "HYD Plant",
                        StartDate = "2025-06-09",
                        EndDate = "2025-06-22"
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
        _logger.LogInformation("Creating supplier profile.");

        SupplierProfileCreationRequestModel? supplierProfileRequest;
        try
        {
            supplierProfileRequest = await System.Text.Json.JsonSerializer.DeserializeAsync<SupplierProfileCreationRequestModel>(
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

        if (supplierProfileRequest == null || string.IsNullOrWhiteSpace(supplierProfileRequest.SupplierName))
        {
            return new BadRequestObjectResult("SupplierName and Tier are required.");
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
            Timestamp = DateTimeOffset.UtcNow
        };

        int partCount = supplierProfileRequest.PartNumbers.Count;
        List<SupplierPartDetail> supplierPartDetailColl = new List<SupplierPartDetail>();
        if (partCount > 0)
        {
            supplierProfileRequest.PartNumbers.ForEach(part =>
            {
                var supplierPartDetails = new SupplierPartDetail
                {
                    PartNumber = part,
                    SupplierId = supplierId
                };
                supplierPartDetailColl.Add(supplierPartDetails);
            });
        }


        try
        {
            await _tableService.AddEntityAsync("SupplierProfiles", profileEntity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing supplier profile to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            await _tableService.AddEntityAsync("SupplierPlants", supplierPlantEntity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing supplier plant details to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        try
        {
            await _tableService.AddEntitiesAsync("SupplierPartDetails", supplierPartDetailColl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing supplier part numbers to Azure Table Storage.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        SupplierProfileCreationResponseModel profilecreationResponse = new SupplierProfileCreationResponseModel
        {
            SupplierId = supplierId,
            PlantId = plantId,
            SupplierName = supplierProfileRequest.SupplierName
        };

        // To-Do: Code snippet to post the supplier profile creation message (with data) to Azure storage queues
        // To-Do: For the time being we can create the Supplier node from here (to be refactored sooner than later)
        // To-Do: The leadtime is hardcoded for now (20 days ~ 3 weeks). This has to be gotten from the supplier, through progressive profiling
        // Note: The supplier might be having different lead times for different parts and the plants they supply to
        // To-Do: Pertaining to the issue mentioned in the previous point, we need to add a 3 way connection between the part#, deliveryPlant and the leadtime value
        await CreateSubtierSupplierGraphNodeAsync(new SubtierSupplierDTO
        {
            LeadTimeInDays = 20, 
            PartNumbers = supplierProfileRequest.PartNumbers,
            SupplierId = supplierId,
            SupplierName = supplierProfileRequest.SupplierName,
            Tier = string.IsNullOrEmpty(supplierProfileRequest.Tier)? "Tier-N" : supplierProfileRequest.Tier
        });

        return new OkObjectResult(new { profilecreationResponse, message = "Supplier profile created successfully." });
    }

    // To-Do: This method needs to moved to Core + Infra and called from a function app that listens to an Azure Storage Queue
    private async Task<bool> CreateSubtierSupplierGraphNodeAsync(SubtierSupplierDTO subtierSupplier)
    {

        // Note: A new part node is being created that corresponds to the suppliers local part naming convention
        // To-Do: An edge needs to be created that connects the supplier with the appropriate BOM node.
        // To-Do: This can be implemented only if we have a process that does the mapping between the local and OEM part numbers. This is yet to be figured out

        // Note: This involves some amount of complexity - Depending on the tier of the supplier we select 
        // BomItem, BomSubitem, Component or ComponentRawMaterial in the node filer condition
        // This will follow the same logic as the REGION code that is commented for now

        // Note: This code is based on Neo4j .net driver. Lift and shift to Infra wont work
        // To-Do : We need to change the code to use Neo4j .net client. The DTO also needs to be moved to the infra project

        // Cypher query to create SubtierSupplier node and connect part numbers as separate nodes
        // To-Do: Enhance the query to create the supplier plant nodes (rationale: plant monitoring using Resilinc/Everstream)
        var cypher = @"
            CREATE (s:SubtierSupplier {
                SupplierId: $supplierId,
                SupplierName: $supplierName,
                LeadTimeInDays: $leadTimeInDays,
                Tier: $supplierTier
            })
            WITH s
            UNWIND $partNumbers AS partNumber
            MERGE (p:SupplierPart {PartNumber: partNumber})
            MERGE (s)-[:SUPPLIES]->(p)
            RETURN s
        ";

        var parameters = new Dictionary<string, object>
        {
            { "supplierId", subtierSupplier.SupplierId },
            { "supplierName", subtierSupplier.SupplierName },
            { "leadTimeInDays", subtierSupplier.LeadTimeInDays ?? 0 },
            { "partNumbers", subtierSupplier.PartNumbers },
            { "supplierTier", subtierSupplier.Tier }
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
            var result = await session.RunAsync(cypher, parameters);
            var createdNode = await result.SingleAsync();

            // Future enhancement: Add more supplier attributes and relationships as needed

            return createdNode != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SubtierSupplier node in Neo4j.");
            return false;
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

