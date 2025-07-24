public class RiskCalculationRequest
{
    [JsonProperty("solution_coverage_quantity")]
    public int SolutionCoverageQuantity { get; set; }

    [JsonProperty("stockout_penalty_rate")]
    public float StockoutPenaltyRate { get; set; }
}