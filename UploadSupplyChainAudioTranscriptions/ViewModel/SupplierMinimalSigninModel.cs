using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{
    public class SupplierMinimalSigninModel
    {
        public required string SupplierName { get; set; }
        public required string SupplierId { get; set; }
        public required string PlantId { get; set; }
    }
}
