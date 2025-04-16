using Microsoft.Azure.Cosmos;
using System.Collections.Generic;
using System.Threading.Tasks;
using Models;


// Service to get Db-related data from UserSecrets.
public class CosmosDbService
{
    public Container LogContainer { get; }
    public Container RulesContainer { get; }

    public CosmosDbService(string endpointUri, string primaryKey, string databaseId, string logContainerId, string ruleContainerId)
    {
        var cosmosClient = new CosmosClient(endpointUri, primaryKey);
        var database = cosmosClient.GetDatabase(databaseId);

        LogContainer = database.GetContainer(logContainerId);
        RulesContainer = database.GetContainer(ruleContainerId);
    }

    // Fetches rate limit rules from Cosmos DB
    public async Task<List<RateLimitRule>> GetRateLimitRulesAsync()
    {
        var rateLimitRules = new List<RateLimitRule>();

        try
        {
            var query = RulesContainer.GetItemQueryIterator<RateLimitRule>("SELECT * FROM c");
            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                rateLimitRules.AddRange(response);
            }
        }
        catch (CosmosException ex)
        {
            Console.WriteLine($"[CosmosDb Error] {ex.Message}");
        }
        return rateLimitRules;
    }

    public async Task DeleteAllRulesAsync()
{
    try
    {
        var query = RulesContainer.GetItemQueryIterator<RateLimitRule>("SELECT * FROM c");

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync();
            foreach (var rule in response)
            {
                await RulesContainer.DeleteItemAsync<RateLimitRule>(
                    rule.id,
                    new PartitionKey(rule.id)
                );
            }
        }

        Console.WriteLine("All rate limit rules have been deleted from CosmosDB.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DeleteRules Error] Failed to delete rules: {ex.Message}");
    }
}

}
