using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UploadSupplyChainAudioTranscriptions.Entities;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities
{
    internal class SubtierSupplierDTO
    {
        public required string SupplierName { get; set; }
        public required string SupplierId { get; set; }

        // Updated to include complete supplier part details instead of just part numbers
        public required List<SupplierPart> SupplierParts { get; set; }

        // This has to be filled during progressive profiling
        public int? LeadTimeInDays { get; set; }
        public string? Tier { get; set; }
    }
}
