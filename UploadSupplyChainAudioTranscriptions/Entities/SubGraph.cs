using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities
{
    using System.Collections.Generic;

    public class RawMaterial
    {
        public string Name { get; set; }
    }

    public class Component
    {
        public string Name { get; set; }
        public RawMaterial MadeOf { get; set; }
    }

    public class Subassembly
    {
        public string Id { get; set; }
        public List<Component> Components { get; set; } = new List<Component>();
    }

    public class Assembly
    {
        public string Name { get; set; }
        public List<Subassembly> Subassemblies { get; set; } = new List<Subassembly>();
    }

    public class Plant
    {
        public string Name { get; set; }
        public List<Assembly> Assemblies { get; set; } = new List<Assembly>();
    }
}
