using CsvHelper.Configuration;
using UploadSupplyChainAudioTranscriptions.Entities;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public sealed class SupplyChainDataMap : ClassMap<SupplyChainData>
{
    public SupplyChainDataMap()
    {
        Map(m => m.Timestamp);
        Map(m => m.Tier);
        Map(m => m.SupplierID);
        Map(m => m.Stage);
        Map(m => m.SupplierPartJson).Name("Material").Convert(args => 
        {
            // Handle CSV parsing for SupplierPart - expecting format like "PartNumber:PartName"
            var materialValue = args.Row.GetField("Material");
            if (string.IsNullOrEmpty(materialValue))
                return string.Empty;
            var partData = materialValue.Split(':');
            var supplierPart = new SupplierPart
            {
                SupplierPartNumber = partData.Length > 0 ? partData[0] : string.Empty,
                SupplierPartName = partData.Length > 1 ? partData[1] : partData[0]
            };
            return JsonConvert.SerializeObject(supplierPart);
        });
        Map(m => m.Status);
        Map(m => m.QtyPlanned);
        Map(m => m.QtyFromInventory);
        Map(m => m.QtyProcured);
        Map(m => m.QtyProduced);
        Map(m => m.QtyRemaining);
        Map(m => m.PlannedStartDate);
        Map(m => m.PlannedCompletionDate);
        //Map(m => m.Voice_EN);
        //Map(m => m.Voice_HI);
        //Map(m => m.Voice_Hinglish);
        Map(m => m.RippleEffect);
    }
}