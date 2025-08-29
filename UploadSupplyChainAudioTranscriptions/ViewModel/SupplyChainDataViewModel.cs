using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UploadSupplyChainAudioTranscriptions.Entities;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{

    public class SupplyChainDataViewModel
    {
        public string Tier { get; set; }
        public string SupplierID { get; set; }
        public string Stage { get; set; }
        public List<SupplierPart> SupplierParts { get; set; } = new List<SupplierPart>();
        public string Status { get; set; }
        public double? QuantityPlanned { get; set; }
        public double? QuantityFromInventory { get; set; }
        public double? QuantityProcured { get; set; }
        public double? QuantityProduced { get; set; }
        public double? QuantityRemaining { get; set; }
        public DateTimeOffset? PlannedStartDate { get; set; }
        public DateTimeOffset? PlannedCompletionDate { get; set; }
        public string RippleEffect { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public string BarColor { get; set; }
    }
}
