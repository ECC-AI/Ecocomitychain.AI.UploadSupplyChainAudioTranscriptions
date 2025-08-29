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
        Map(m => m.SupplierPartsJson).Name("Material").Convert(args => 
        {
            // Handle CSV parsing for SupplierParts - assuming format like "PartNumber1:PartName1;PartNumber2:PartName2"
            var materialValue = args.Row.GetField("Material");
            if (string.IsNullOrEmpty(materialValue))
                return string.Empty;
            
            var supplierParts = materialValue.Split(';')
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => 
                {
                    var partData = part.Split(':');
                    return new SupplierPart
                    {
                        SupplierPartNumber = partData.Length > 0 ? partData[0] : string.Empty,
                        SupplierPartName = partData.Length > 1 ? partData[1] : partData[0]
                    };
                })
                .ToList();
                
            return JsonConvert.SerializeObject(supplierParts);
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