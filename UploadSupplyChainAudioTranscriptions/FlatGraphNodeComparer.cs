using Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.Entities;

namespace UploadSupplyChainAudioTranscriptions
{
    internal class FlatGraphNodeComparer : IEqualityComparer<FlatGraphNode>
    {
        public bool Equals(FlatGraphNode? x, FlatGraphNode? y)
        {
            if (x == null || y == null) return false;
            return x.id == y.id &&
                   (x.parents == null && y.parents == null ||
                    x.parents != null && y.parents != null && x.parents.SequenceEqual(y.parents));
        }
        public int GetHashCode(FlatGraphNode obj)
        {
            int hash = obj.id.GetHashCode();
            if (obj.parents != null)
            {
                foreach (var p in obj.parents)
                    hash ^= p.GetHashCode();
            }
            return hash;
        }
    }
}