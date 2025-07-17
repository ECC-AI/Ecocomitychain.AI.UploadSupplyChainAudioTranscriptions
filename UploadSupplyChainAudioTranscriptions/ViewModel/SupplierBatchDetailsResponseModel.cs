using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{
    public class SupplierBatchDetailsResponseModel
    {
        public required string SupplierId { get; set; }
        public required List<string> BatchNumbers { get; set; }
    }
}
