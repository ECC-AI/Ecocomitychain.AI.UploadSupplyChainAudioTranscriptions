using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UploadSupplyChainAudioTranscriptions.Entities
{
    public class SupplyChainData : TableEntity
    {
        public required string Tier { get; set; }
        public required string SupplierID { get; set; }
        public required string Stage { get; set; }
        public required string Material { get; set; }
        public required string Status { get; set; }

        public int? QtyPlanned { get; set; }
        public int? QtyFromInventory { get; set; }
        public int? QtyProcured { get; set; }
        public int? QtyProduced { get; set; }
        public int? QtyRemaining { get; set; }

        public required string Voice_EN { get; set; }
        public required string Voice_HI { get; set; }
        public required string Voice_Hinglish { get; set; }
        public required string RippleEffect { get; set; }
    }

}
