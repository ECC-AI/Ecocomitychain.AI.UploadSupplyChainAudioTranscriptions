using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

public class RiskCalculationRequest
{
    [JsonProperty("solution_coverage_quantity")]
    public int SolutionCoverageQuantity { get; set; }

    [JsonProperty("stockout_penalty_rate")]
    public float StockoutPenaltyRate { get; set; }
}