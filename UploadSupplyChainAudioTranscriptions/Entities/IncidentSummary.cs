using Newtonsoft.Json;
using System.Collections.Generic;

namespace UploadSupplyChainAudioTranscriptions.Entities
{
    public class IncidentSummary
    {
        [JsonProperty("incident_id")]
        public string IncidentId { get; set; } = string.Empty;

        [JsonProperty("detected_at")]
        public string DetectedAt { get; set; } = string.Empty;

        [JsonProperty("sector")]
        public string Sector { get; set; } = string.Empty;

        [JsonProperty("subsector")]
        public string Subsector { get; set; } = string.Empty;

        [JsonProperty("component")]
        public ComponentDetails Component { get; set; } = new ComponentDetails();

        [JsonProperty("lead_time_slip_weeks")]
        public int LeadTimeSlipWeeks { get; set; }

        [JsonProperty("lead_time_slip_tolerance_weeks")]
        public int LeadTimeSlipToleranceWeeks { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; } = string.Empty;

        [JsonProperty("root_cause_hypothesis")]
        public string RootCauseHypothesis { get; set; } = string.Empty;

        [JsonProperty("urgency")]
        public string Urgency { get; set; } = string.Empty;

        [JsonProperty("horizon_weeks")]
        public int HorizonWeeks { get; set; }

        [JsonProperty("impact_tier")]
        public int ImpactTier { get; set; }

        [JsonProperty("oem_Production_BatchWeek")]
        public string OemProductionBatchWeek { get; set; } = string.Empty;

        [JsonProperty("impact_Material_Category")]
        public string ImpactMaterialCategory { get; set; } = string.Empty;

        [JsonProperty("impact_Plant")]
        public string ImpactPlant { get; set; } = string.Empty;

        [JsonProperty("free_text")]
        public string FreeText { get; set; } = string.Empty;
    }

    public class ComponentDetails
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("units_per_vehicle")]
        public int UnitsPerVehicle { get; set; }

        [JsonProperty("taxonomy")]
        public List<string> Taxonomy { get; set; } = new List<string>();
    }
}
