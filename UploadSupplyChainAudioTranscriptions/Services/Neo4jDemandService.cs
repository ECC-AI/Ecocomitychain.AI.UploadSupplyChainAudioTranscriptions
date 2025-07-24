using System;
using System.Threading.Tasks;
using Neo4j.Driver;

namespace UploadSupplyChainAudioTranscriptions.Services
{
    public class Neo4jService : INeo4jService
    {
        private readonly string _uri;
        private readonly string _user;
        private readonly string _password;

        public Neo4jService()
        {
            _uri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? string.Empty;
            _user = Environment.GetEnvironmentVariable("NEO4J_USER") ?? string.Empty;
            _password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? string.Empty;
        }

        public async Task<int> GetDemandQuantityAsync(string material, string plant, string periodStart, string periodEnd)
        {
            if (string.IsNullOrWhiteSpace(_uri) || string.IsNullOrWhiteSpace(_user) || string.IsNullOrWhiteSpace(_password))
                throw new InvalidOperationException("Neo4j connection information is missing in environment variables.");

            int demandQuantity = 0;
            var driver = GraphDatabase.Driver(_uri, AuthTokens.Basic(_user, _password));
            var session = driver.AsyncSession();
            try
            {
                var cypher = @"
                                MATCH (sd:MRPSupplyDemand) 
                                WHERE sd.Material = $material AND 
                                sd.MRPPlant = $plant AND 
                                sd.PeriodOrSegment >= $periodStart AND 
                                sd.PeriodOrSegment <= $periodEnd
                                RETURN sum(sd.MRPElementOpenQuantity) AS totalDemand
                            ";
                var parameters = new { material, plant, periodStart, periodEnd };
                var cursor = await session.RunAsync(cypher, parameters);
                var results = await cursor.ToListAsync();
                var result = results.SingleOrDefault();
                if (result != null && result["totalDemand"] != null && int.TryParse(result["totalDemand"].ToString(), out int totalDemand))
                {
                    demandQuantity = totalDemand;
                }
            }
            finally
            {
                await session.CloseAsync();
            }
            return demandQuantity;
        }

        public async Task<float> GetAverageSellingPriceAsync(string material, string supplierContains)
        {
            if (string.IsNullOrWhiteSpace(_uri) || string.IsNullOrWhiteSpace(_user) || string.IsNullOrWhiteSpace(_password))
                throw new InvalidOperationException("Neo4j connection information is missing in environment variables.");

            float avgPrice = 0;
            var driver = GraphDatabase.Driver(_uri, AuthTokens.Basic(_user, _password));
            var session = driver.AsyncSession();
            try
            {
                var cypher = @"
                                MATCH (pc:PurchaseContract)-[:HAS_CONTRACT_ITEM]->(pci:PurchaseContractItem)
                                WHERE pc.Supplier CONTAINS $supplierContains AND 
                                pci.Material = $material
                                RETURN avg(pc.PurchaseContractTargetAmount) AS average_target_amount
                            ";
                var parameters = new { material, supplierContains };
                var cursor = await session.RunAsync(cypher, parameters);
                var results = await cursor.ToListAsync();
                var result = results.SingleOrDefault();
                if (result != null && result["average_target_amount"] != null && float.TryParse(result["average_target_amount"].ToString(), out float avg))
                {
                    avgPrice = avg;
                }
            }
            finally
            {
                await session.CloseAsync();
            }
            return avgPrice;
        }
    }
    
}
