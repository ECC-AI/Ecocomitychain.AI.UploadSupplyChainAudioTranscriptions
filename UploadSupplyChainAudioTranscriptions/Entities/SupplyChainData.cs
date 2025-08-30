using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace UploadSupplyChainAudioTranscriptions.Entities
{
    public class SupplyChainData : TableEntity, ITableEntity
    {
        public SupplyChainData() { }
        public string Tier { get; set; }
        public string SupplierID { get; set; }
        public string Stage { get; set; }
        
        // Store as JSON string for Azure Table Storage compatibility
        public string SupplierPartJson { get; set; }

        // Property for easy access to SupplierPart (not stored in table)
        [IgnoreProperty]
        public SupplierPart SupplierPart
        {
            get
            {
                if (string.IsNullOrEmpty(SupplierPartJson))
                    return null;
                try
                {
                    return JsonConvert.DeserializeObject<SupplierPart>(SupplierPartJson);
                }
                catch
                {
                    return null;
                }
            }
            set
            {
                SupplierPartJson = value != null ? JsonConvert.SerializeObject(value) : string.Empty;
            }
        }
        
        public string Status { get; set; }

        public double? QtyPlanned { get; set; }
        public double? QtyFromInventory { get; set; }
        public double? QtyProcured { get; set; }
        public double? QtyProduced { get; set; }
        public double? QtyRemaining { get; set; }

        public DateTimeOffset? PlannedStartDate { get; set; }
        public DateTimeOffset? PlannedCompletionDate { get; set; }

        //public string Voice_EN { get; set; }
        //public string Voice_HI { get; set; }
        //public string Voice_Hinglish { get; set; }
        public string RippleEffect { get; set; }
        public DateTimeOffset? ReportedTime { get; set; }

    }

}
