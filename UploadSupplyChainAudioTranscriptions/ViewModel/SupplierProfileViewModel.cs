using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Table;
using UploadSupplyChainAudioTranscriptions.Entities;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{
    /// <summary>
    /// Model representing the data that the Mobile app sends from the Supplier Onboarding screen
    /// </summary>
    public class SupplierProfileCreationRequestModel
    {
        public required string SupplierName { get; set; }
        
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

        public required string PlantName { get; set; }
        public string? Tier { get; set; } // Progressive Profiling

        public int LeadTimeInDays { get; set; }

    }

    public class SupplierProfileCreationResponseModel
    {
        public required string SupplierName { get; set; }
        public required string SupplierId { get; set; }
        public required string PlantId { get; set; }

    }
}
