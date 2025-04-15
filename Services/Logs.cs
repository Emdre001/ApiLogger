using System;

namespace Models;
public class ApiLogEntry
{
    public string id { get; set; } // Cosmos DB requires an "id" field.
    public string HttpMethod { get; set; }
    public string Path { get; set; }
    public string Controller { get; set; }
    public string UserId { get; set; }
    public string IpAddress { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime StopTime { get; set; }
    public double DurationMs { get; set; }
}
