using Microsoft.Azure.Cosmos;
using Models;

namespace getlogs;

public class GetLogService
{
    private readonly Container _container;

    public GetLogService(CosmosDbService cosmosDbService)
    {
        _container = cosmosDbService.LogContainer;
    }

    // Service to fetch Logs from CosmosDB.
    public async Task<List<ApiLogEntry>> GetLogsAsync(TimeSpan withinLast)
    {
        var logs = new List<ApiLogEntry>();
        var since = DateTime.UtcNow - withinLast;

        Console.WriteLine($"[DEBUG] Fetching logs since: {since:u}");

        var query = new QueryDefinition("SELECT * FROM c WHERE c.StartTime >= @since")
            .WithParameter("@since", since);

        var iterator = _container.GetItemQueryIterator<ApiLogEntry>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            logs.AddRange(response);
            Console.WriteLine($"[DEBUG] Batch fetched: {response.Count} logs");
        }

        Console.WriteLine($"[DEBUG] Total logs fetched: {logs.Count}");
        return logs;
    }
}
