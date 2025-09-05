using Microsoft.Azure.Cosmos;
using UploadSupplyChainAudioTranscriptions.Entities;

namespace UploadSupplyChainAudioTranscriptions.Services
{
    public interface ICosmosDbService
    {
        Task<string> StoreSupplyChainWarningAsync(SupplyChainData supplyChainData);
        Task<string> StoreSupplyChainWarningAsync(SupplyChainWarning supplyChainWarning);
        Task<List<string>> StoreSupplyChainWarningAsync(List<SupplyChainWarning> supplyChainWarnings);
        Task<string> StoreRiskAcknowledgmentAsync(string incidentId, object riskAcknowledgment);
    }
}
