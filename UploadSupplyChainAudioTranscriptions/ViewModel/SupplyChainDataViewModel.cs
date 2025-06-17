using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{

    public class SupplyChainDataViewModel
    {
        public string Tier { get; set; }
        public string SupplierID { get; set; }
        public string Stage { get; set; }
        public string Material { get; set; }
        public string Status { get; set; }
        public double? QuantityPlanned { get; set; }
        public double? QuantityFromInventory { get; set; }
        public double? QuantityProcured { get; set; }
        public double? QuantityProduced { get; set; }
        public double? QuantityRemaining { get; set; }
        public string RippleEffect { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public string BarColor { get; set; }
    }
}
