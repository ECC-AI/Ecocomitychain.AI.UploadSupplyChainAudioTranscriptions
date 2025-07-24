using System.Threading.Tasks;

namespace UploadSupplyChainAudioTranscriptions.Services
{
    public interface INeo4jService
    {
        Task<int> GetDemandQuantityAsync(string material, string plant, string periodStart, string periodEnd);
        Task<float> GetAverageSellingPriceAsync(string material, string supplierContains);
    }
}
