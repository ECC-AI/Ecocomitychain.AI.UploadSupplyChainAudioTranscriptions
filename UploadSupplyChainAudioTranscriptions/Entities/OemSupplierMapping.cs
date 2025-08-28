using Azure;
using Azure.Data.Tables;
using System;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities
{
    /// <summary>
    /// Entity representing the mapping between OEM parts and supplier parts
    /// This enables traceability between OEM part numbers and corresponding supplier part numbers
    /// </summary>
    public class OemSupplierMapping : ITableEntity
    {
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string SupplierPartNumber { get; set; } = string.Empty;
        public string OemPartNumber { get; set; } = string.Empty;
        public string OemPartName { get; set; } = string.Empty;
        public string SupplierId { get; set; } = string.Empty;
        public string SupplierPartName { get; set; } = string.Empty;

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => SupplierPartNumber;
            set => SupplierPartNumber = value;
        }

        public string RowKey
        {
            get => SupplierId;
            set => SupplierId = value;
        }
    }
}
