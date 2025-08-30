using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using UploadSupplyChainAudioTranscriptions.Entities;
using Newtonsoft.Json;

namespace UploadSupplyChainAudioTranscriptions.Entities
{
    public class SupplyChainWarning
    {
        [JsonProperty("id")]
        public string Id { get; set; }
        public string PartitionKey { get; set; } = string.Empty;

        [JsonProperty("SupplierId")]
        public string? Supplier { get; set; }
        public string? Tier { get; set; }
        public string? Stage { get; set; }
        public SupplierPart? SupplierPart { get; set; }
        public OemPart? OemPart { get; set; }
        public string? Status { get; set; }
        public string? RippleEffect { get; set; }
        public DateTimeOffset? PlannedStartDate { get; set; }
        public DateTimeOffset? PlannedCompletionDate { get; set; }
        public DateTimeOffset? ReportedTime { get; set; }
        
        // Data from ImpactedNodeCount (QueryImpactedNodeCount)
        public string? ImpactedNode { get; set; }
        public int? ComponentRawMaterialCount { get; set; }
        public int? ComponentCount { get; set; }
        public int? BomSubItemCount { get; set; }
        public int? BomItemCount { get; set; }
        public int? MaterialBOMCount { get; set; }
        
        // Additional metadata for the warning
        public string WarningType { get; set; } = "SupplyChainDisruption";
        public string Source { get; set; } = "CombinedAnalysis";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string DocumentType { get; set; } = "SupplyChainWarning";
    }
}
