using CsvHelper.Configuration;
using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Map
{
    /// <summary>
    /// CSV mapping configuration for OEM Supplier Mapping data import
    /// Maps CSV columns to OemSupplierMapping entity properties
    /// </summary>
    public sealed class OemSupplierMappingMap : ClassMap<OemSupplierMapping>
    {
        public OemSupplierMappingMap()
        {
            Map(m => m.OemPartNumber).Name("OEM_PartNumber");
            Map(m => m.OemPartName).Name("OEM_PartName");
            Map(m => m.SupplierId).Name("SupplierId");
            Map(m => m.SupplierPartNumber).Name("SupplierPartNumber");
            Map(m => m.SupplierPartName).Name("SupplierPartName");
        }
    }
}
