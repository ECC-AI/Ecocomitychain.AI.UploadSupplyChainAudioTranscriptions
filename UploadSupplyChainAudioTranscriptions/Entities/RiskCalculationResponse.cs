public class RiskCalculationResponse
{
    [JsonProperty("units_at_risk")]
    public int UnitsAtRisk { get; set; }

    [JsonProperty("revenue_at_risk")]
    public float RevenueAtRisk { get; set; }

    [JsonProperty("stockout_penalty")]
    public float StockoutPenalty { get; set; }
}
