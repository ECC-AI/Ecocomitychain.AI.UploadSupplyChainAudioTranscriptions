using CsvHelper.Configuration;
using UploadSupplyChainAudioTranscriptions.Entities;

public sealed class SupplyChainDataMap : ClassMap<SupplyChainData>
{
    public SupplyChainDataMap()
    {
        Map(m => m.Timestamp);
        Map(m => m.Tier);
        Map(m => m.SupplierID);
        Map(m => m.Stage);
        Map(m => m.Material);
        Map(m => m.Status);
        Map(m => m.QtyPlanned);
        Map(m => m.QtyFromInventory);
        Map(m => m.QtyProcured);
        Map(m => m.QtyProduced);
        Map(m => m.QtyRemaining);
        //Map(m => m.Voice_EN);
        //Map(m => m.Voice_HI);
        //Map(m => m.Voice_Hinglish);
        Map(m => m.RippleEffect);
    }
}