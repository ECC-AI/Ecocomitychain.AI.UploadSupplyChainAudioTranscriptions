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
                    oempart = supplyChainData.OemPart,
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

        public async Task<List<string>> StoreSupplyChainWarningAsync(List<SupplyChainWarning> supplyChainWarnings)
        {
            var documentIds = new List<string>();
            var errors = new List<string>();

            if (supplyChainWarnings == null || !supplyChainWarnings.Any())
            {
                throw new ArgumentException("Supply chain warnings list cannot be null or empty.", nameof(supplyChainWarnings));
            }

            _logger.LogInformation($"Starting batch storage of {supplyChainWarnings.Count} supply chain warnings.");

            foreach (var warning in supplyChainWarnings)
            {
                try
                {
                    if (warning == null)
                    {
                        _logger.LogWarning("Skipping null supply chain warning in the list.");
                        continue;
                    }

                    // Set partition key if not already set
                    if (string.IsNullOrEmpty(warning.PartitionKey))
                    {
                        warning.PartitionKey = warning.Supplier ?? "unknown";
                    }

                    // Store the supply chain warning document in Cosmos DB
                    var response = await _container.CreateItemAsync(
                        warning,
                        new PartitionKey(warning.PartitionKey));

                    documentIds.Add(warning.Id);
                    _logger.LogInformation($"Successfully stored supply chain warning with ID: {warning.Id}");
                }
                catch (CosmosException ex)
                {
                    var errorMessage = $"Cosmos DB error while storing supply chain warning {warning?.Id}: {ex.Message}";
                    _logger.LogError(ex, errorMessage);
                    errors.Add(errorMessage);
                }
                catch (Exception ex)
                {
                    var errorMessage = $"Unexpected error while storing supply chain warning {warning?.Id}: {ex.Message}";
                    _logger.LogError(ex, errorMessage);
                    errors.Add(errorMessage);
                }
            }

            _logger.LogInformation($"Batch storage completed. Successfully stored: {documentIds.Count}, Errors: {errors.Count}");

            if (errors.Any())
            {
                var combinedErrorMessage = string.Join("; ", errors);
                throw new Exception($"Some warnings failed to store. Errors: {combinedErrorMessage}");
            }

            return documentIds;
        }

        public async Task<string> StoreRiskAcknowledgmentAsync(string incidentId, object riskAcknowledgment)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(incidentId))
                {
                    throw new ArgumentNullException(nameof(incidentId));
                }

                if (riskAcknowledgment == null)
                {
                    throw new ArgumentNullException(nameof(riskAcknowledgment));
                }

                // Get the supplyChainRisks container
                var risksContainer = _cosmosClient.GetContainer(DatabaseName, "supplyChainRisks");

                // Create a document with additional metadata for Cosmos DB
                var riskDocument = new
                {
                    id = Guid.NewGuid().ToString(),
                    partitionKey = incidentId,
                    incident_id = incidentId,
                    riskAcknowledgment = riskAcknowledgment,
                    acknowledgedAt = DateTimeOffset.UtcNow,
                    documentType = "RiskAcknowledgment"
                };

                // Store the document in Cosmos DB
                var response = await risksContainer.CreateItemAsync(
                    riskDocument,
                    new PartitionKey(riskDocument.partitionKey));

                _logger.LogInformation($"Successfully stored risk acknowledgment with ID: {riskDocument.id} for incident: {incidentId}");
                return riskDocument.id;
            }
            catch (CosmosException ex)
            {
                _logger.LogError(ex, $"Cosmos DB error while storing risk acknowledgment: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error while storing risk acknowledgment: {ex.Message}");
                throw;
            }
        }
    }
}
