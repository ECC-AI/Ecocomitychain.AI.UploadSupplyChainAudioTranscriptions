using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ecocomitychain.AI.UploadSupplyChainAudioTranscriptions.ViewModel
{
    internal class SupplyShortageSummaryViewModel
    {
        public required string material { get; set; }
        public required int numPartsPerVehicle { get; set; }  // add more properties when we are dealing with scenarios having shortage of more than one part

        public required List<WeeklyForecast> weeklyForecast { get; set; }
    }


    internal class WeeklyForecast
    {
        public int weekNum { get; set; }
        public int fgDemandQuantity { get; set; }
        public int numPartsNeeded { get; set; }
    }
}
