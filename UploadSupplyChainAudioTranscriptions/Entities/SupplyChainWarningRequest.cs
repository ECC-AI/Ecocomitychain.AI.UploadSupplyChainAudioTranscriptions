using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UploadSupplyChainAudioTranscriptions.Entities
{
    public class SupplyChainWarningRequest
    {
        public string SupplierId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ImpactedNode { get; set; } = string.Empty;
    }
}
