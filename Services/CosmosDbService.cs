using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;

public class CosmosDbService
{
    private readonly Container _container;

    public CosmosDbService(string endpointUri, string primaryKey, string databaseId, string containerId)
    {
        var cosmosClient = new CosmosClient(endpointUri, primaryKey);
        _container = cosmosClient.GetContainer(databaseId, containerId);
    }


}
