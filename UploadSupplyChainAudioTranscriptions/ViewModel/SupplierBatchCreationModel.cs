using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{
    public class SupplierBatchCreationModel
    {
        public required string SupplierId { get; set; }
        public required string PlantId { get; set; }
        public DateTime BatchStartDate { get; set; }
        public DateTime BatchEndDate { get; set; }
        public string? OEMScheduleNumber { get; set; }
        public required string  SupplierBatchNumber { get; set; }
    }
}
