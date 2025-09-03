using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities
{
    public class SupplierProfileBase : ITableEntity
    {
        public string SupplierName { get; set; }
        public string SupplierId { get; set; }
        public string? Tier { get; set; }

        // This has to be filled during progressive profiling
        public int? LeadTimeInDays { get; set; }

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => SupplierId;
            set => SupplierId = value;
        }

        public string RowKey
        {
            get => SupplierName;
            set => SupplierName = value;
        }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;
    }

    /// <summary>
    /// IMP NOTE: The Plants become first class citizens here 
    /// We will be able to plot the supplier plant in the graph 
    /// We will be able to map the disruption alerts from resilinc/everstream to the supplier plants
    /// </summary>
    public class SupplierPlant : ITableEntity
    {
        public string SupplierId { get; set; }
        public string PlantId { get; set; }
        public string PlantName { get; set; }
        public SupplierPlantAddress? PlantAddress { get; set; }

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => SupplierId;
            set => SupplierId = value;
        }

        public string RowKey
        {
            get => PlantId;
            set => PlantId = value;
        }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;
    }

    public class SupplierPlantAddress
    {
        public required string Street { get; set; }
        public required string City { get; set; }
        public required string State { get; set; }
        public required string Country { get; set; }
        public required string PostalCode { get; set; }
        public required double Latitude { get; set; }
        public required double Longitude { get; set; }
    }

    public class SupplierDeliveryPlantAddress
    {
        public required string Street { get; set; }
        public required string City { get; set; }
        public required string State { get; set; }
        public required string Country { get; set; }
        public required string PostalCode { get; set; }
        public required double Latitude { get; set; }
        public required double Longitude { get; set; }
    }

    public class SupplierDeliveryPlant : ITableEntity
    {
        public string SupplierId { get; set; }
        public string DeliveryPlantId { get; set; }
        public string DeliveryPlantName { get; set; }
        public SupplierDeliveryPlantAddress? DeliveryPlantAddress { get; set; }

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => SupplierId;
            set => SupplierId = value;
        }

        public string RowKey
        {
            get => DeliveryPlantId;
            set => DeliveryPlantId = value;
        }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;
    }

    public class SupplierPartDetail : ITableEntity
    {
        public string SupplierId { get; set; }
        public string? PartNumber { get; set; }
        public string? PartDescription { get; set; }

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => SupplierId;
            set => SupplierId = value;
        }

        public string RowKey
        {
            get => PartNumber;
            set => PartNumber = value;
        }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;
    }

    public class PartMappingMasterData : ITableEntity
    {
        public string SupplierId { get; set; }

        // This is supposed to be MaterialBOM.Material or Product.Product from OEM's Data
        public string ProductId { get; set; } 
        public string SupplierPartNumber { get; set; }
        public string OEMPartNumber { get; set; }

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => ProductId;
            set => ProductId = value;
        }

        public string RowKey
        {
            get => OEMPartNumber;
            set => OEMPartNumber = value;
        }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;
    }

    public class SupplierBatchDetails : ITableEntity
    {
        public string SupplierId { get; set; }
        public string PlantId { get; set; }
        public string BatchId { get; set; }
        public string SupplierBatchNumber { get; set; } // This is the batch number assigned by the supplier
        public DateTime BatchStartDate { get; set; }
        public DateTime BatchEndDate { get; set; }
        public string? OEMScheduleNumber { get; set; } // This has been marked as nullable for now. Make this required once we know the mapping logic
        public SupplierBatchStatus? BatchStatus { get; set; } // Add more status flags when needed. Update the flags during the lifecycle events

        // Azure Table Storage properties
        public string PartitionKey
        {
            get => SupplierId;
            set => SupplierId = value;
        }

        public string RowKey
        {
            get => BatchId;
            set => BatchId = value;
        }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; } = ETag.All;
    }

    public enum SupplierBatchStatus
    {
        ACTIVE,
        COMPLETED,
        ONHOLD
    }


}
