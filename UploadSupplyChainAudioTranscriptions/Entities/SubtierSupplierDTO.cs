using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities
{
    internal class SubtierSupplierDTO
    {
        public required string SupplierName { get; set; }
        public required string SupplierId { get; set; }

        // Note: (To-Do) This is a list of part numbers for now. If the part details are to be included, then we need to flatten the 
        // Part details object and create a separate node in the graph
        public required List<string> PartNumbers { get; set; }

        // This has to be filled during progressive profiling
        public int? LeadTimeInDays { get; set; }
    }
}
