using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using UploadSupplyChainAudioTranscriptions.Entities;
using Newtonsoft.Json;

namespace UploadSupplyChainAudioTranscriptions.Services
{
    public class CosmosDbService : ICosmosDbService
    {
        private readonly CosmosClient _cosmosClient;
        private readonly Container _container;
        private readonly ILogger<CosmosDbService> _logger;
        private const string DatabaseName = "ecc_alerts";
        private const string ContainerName = "supplyChainWarnings";

        public CosmosDbService(CosmosClient cosmosClient, ILogger<CosmosDbService> logger)
        {
            _cosmosClient = cosmosClient;
            _logger = logger;
            
            // Initialize the container
            _container = _cosmosClient.GetContainer(DatabaseName, ContainerName);
        }

        public async Task<string> StoreSupplyChainWarningAsync(SupplyChainData supplyChainData)
        {
            try
            {
                if (supplyChainData == null)
                {
                    throw new ArgumentNullException(nameof(supplyChainData));
                }

                // Create a document with additional metadata for Cosmos DB
                var warningDocument = new
                {
                    id = Guid.NewGuid().ToString(),
                    partitionKey = supplyChainData.SupplierID ?? "unknown",
                    tier = supplyChainData.Tier,
                    supplierId = supplyChainData.SupplierID,
                    stage = supplyChainData.Stage,
                    supplierPart = supplyChainData.SupplierPart,
                    status = supplyChainData.Status,
                    qtyPlanned = supplyChainData.QtyPlanned,
                    qtyFromInventory = supplyChainData.QtyFromInventory,
                    qtyProcured = supplyChainData.QtyProcured,
                    qtyProduced = supplyChainData.QtyProduced,
                    qtyRemaining = supplyChainData.QtyRemaining,
                    rippleEffect = supplyChainData.RippleEffect,
                    reportedTime = supplyChainData.ReportedTime,
                    plannedStartDate = supplyChainData.PlannedStartDate,
                    plannedCompletionDate = supplyChainData.PlannedCompletionDate,
                    createdAt = DateTimeOffset.UtcNow,
                    documentType = "SupplyChainWarning"
                };

                // Store the document in Cosmos DB
                var response = await _container.CreateItemAsync(
                    warningDocument,
                    new PartitionKey(warningDocument.partitionKey));

                _logger.LogInformation($"Successfully stored supply chain warning with ID: {warningDocument.id}");
                return warningDocument.id;
            }
            catch (CosmosException ex)
            {
                _logger.LogError(ex, $"Cosmos DB error while storing supply chain warning: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error while storing supply chain warning: {ex.Message}");
                throw;
            }
        }
        public async Task<string> StoreSupplyChainWarningAsync(SupplyChainWarning supplyChainWarning)
        {
            try
            {
                if (supplyChainWarning == null)
                {
                    throw new ArgumentNullException(nameof(supplyChainWarning));
                }

                // Set partition key if not already set
                if (string.IsNullOrEmpty(supplyChainWarning.PartitionKey))
                {
                    supplyChainWarning.PartitionKey = supplyChainWarning.Supplier ?? "unknown";
                }

                // Store the supply chain warning document in Cosmos DB
                var response = await _container.CreateItemAsync(
                    supplyChainWarning,
                    new PartitionKey(supplyChainWarning.PartitionKey));

                _logger.LogInformation($"Successfully stored supply chain warning with ID: {supplyChainWarning.Id}");
                return supplyChainWarning.Id;
            }
            catch (CosmosException ex)
            {
                _logger.LogError(ex, $"Cosmos DB error while storing supply chain warning: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error while storing supply chain warning: {ex.Message}");
                throw;
            }
        }
    }
}
