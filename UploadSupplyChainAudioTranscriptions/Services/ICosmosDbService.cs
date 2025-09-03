using Microsoft.Azure.Cosmos;
using UploadSupplyChainAudioTranscriptions.Entities;

namespace UploadSupplyChainAudioTranscriptions.Services
{
    public interface ICosmosDbService
    {
        Task<string> StoreSupplyChainWarningAsync(SupplyChainData supplyChainData);
        Task<string> StoreSupplyChainWarningAsync(SupplyChainWarning supplyChainWarning);
        Task<string> StoreRiskAcknowledgmentAsync(string incidentId, object riskAcknowledgment);
    }
}
