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
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Globalization;
using System.Text.Json;
using UploadSupplyChainAudioTranscriptions.Entities;
using System.IO;
using System.Net;
using Microsoft.Azure.Functions.Worker.Http;


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
            dataList = await System.Text.Json.JsonSerializer.DeserializeAsync<List<SupplyChainData>>(req.Body, new JsonSerializerOptions
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
}
";

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

            var options = new JsonSerializerOptions
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
                ContractResolver = new CamelCasePropertyNamesContractResolver()
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

}
