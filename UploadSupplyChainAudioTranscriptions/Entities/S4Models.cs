using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities
{
    public interface IGraphNode
    {
        string ParentId { get; set; }
    }


    public class FlatGraphNode
    {
        public string id { get; set; }
        public List<string>? parents { get; set; }
        public string? displaytext { get; set; }

    }

    public class NodeWrapper<T>
    {
        public T Properties { get; set; }
    }
    public class BOMNodeBundle
    {
        public NodeWrapper<S4_ComponentRawMaterial> crm { get; set; }
        public NodeWrapper<S4_Component> c { get; set; }
        public NodeWrapper<S4_BillofMaterialSubItem> bsi { get; set; }
        public NodeWrapper<S4_BillofMaterialItem> bi { get; set; }
        public NodeWrapper<S4_MaterialBOM> mb { get; set; }
    }

    public class S4_ComponentRawMaterial : IGraphNode
    {
        public string ParentId { get; set; }
        public string Name { get; set; }

    }
    public class S4_Component: IGraphNode
    {
        public string ParentId { get; set; }
        public string Name { get; set; }
        public   string Type { get; set; }
        public   string PartNumber { get; set; }
        public   string LifecycleStage { get; set; }
        public string? Material { get; set; } 
        public List<S4_ComponentRawMaterial> RawMaterials { get; set; } = new();
    }

    public class S4_BillofMaterialItem : IGraphNode
    {
        public string ParentId { get; set; }
        public string BillOfMaterial { get; set; }
        public string BillOfMaterialItem { get; set; }
        public string Material { get; set; }
        public string Plant { get; set; }
        public List<S4_BillofMaterialSubItem>? ToMaterialBOMSubItems { get; set; }


    }

    public class S4_BillofMaterialSubItem: IGraphNode
    {
        public string ParentId { get; set; }
        public string BOMSubItemNumberValue { get; set; }
        public string BillofMaterialSubItem { get; set; }
        public string BillOfMaterial { get; set; }
        public string BillOfMaterialItem { get; set; }
        public List<S4_Component> SubAssemblyComponents { get; internal set; }
        public string Material { get; internal set; }
    }

    public class S4_MaterialBOM: IGraphNode
    {
        public string ParentId { get; set; }
        public string BillOfMaterial { get; set; }
        public string Material { get; set; }
        public string Plant { get; set; }
        public List<S4_BillofMaterialItem>? ToMaterialBOMItems { get; set; }

    }

    public class SupplierTimelineItem
    {
        public string Tier { get; set; }
        public string SupplierName { get; set; }
        public string StartDate { get; set; }
        public string EndDate { get; set; }
    }

    public class SupplierTimelineData
    {
        public List<SupplierTimelineItem> Suppliers { get; set; } = new();
    }

    public class SupplierTimelineResponse
    {
        public SupplierTimelineData SupplierTimeline { get; set; } = new();
    }
}
