using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UploadSupplyChainAudioTranscriptions.Entities
{
    public class SupplyChainData : TableEntity, ITableEntity
    {
        public SupplyChainData() { }
        public string Tier { get; set; }
        public string SupplierID { get; set; }
        public string Stage { get; set; }
        public string Material { get; set; }
        public string Status { get; set; }

        public double? QtyPlanned { get; set; }
        public double? QtyFromInventory { get; set; }
        public double? QtyProcured { get; set; }
        public double? QtyProduced { get; set; }
        public double? QtyRemaining { get; set; }

        //public string Voice_EN { get; set; }
        //public string Voice_HI { get; set; }
        //public string Voice_Hinglish { get; set; }
        public string RippleEffect { get; set; }
        public DateTimeOffset? ReportedTime { get; set; }

    }

}
