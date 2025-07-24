using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{
    /// <summary>
    /// Model representing the data that the Mobile app sends from the Supplier Onboarding screen
    /// </summary>
    public class SupplierProfileCreationRequestModel
    {
        public required string SupplierName { get; set; }
        
        public required List<string> PartNumbers { get; set; }
        public required string PlantName { get; set; }
        public string? Tier { get; set; } // Progressive Profiling

    }

    public class SupplierProfileCreationResponseModel
    {
        public required string SupplierName { get; set; }
        public required string SupplierId { get; set; }
        public required string PlantId { get; set; }

    }
}
