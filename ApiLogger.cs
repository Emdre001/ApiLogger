using System.Diagnostics;
using System.Text;
using System;
using Microsoft.Azure.Cosmos;
using Models;
using Microsoft.AspNetCore.Http;

namespace APILoggerProject;

public class ApiLogger
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private Stopwatch _stopwatch;
    private DateTime _startTime;
    private readonly string _controllerName;
    private readonly string _logFilePath;
    private string _userId;
    private string _ipAddress;
    private string _httpMethod;
    private string _path;
    private readonly CosmosDbService _cosmosDbService;
    private List<RateLimitRule> _rateLimitRules = new();

    // In-memory request logs and block records.
    private static readonly Dictionary<string, List<DateTime>> RequestLog = new();
    
    // Constructor
    public ApiLogger(IHttpContextAccessor httpContextAccessor, string controllerName, CosmosDbService cosmosDbService)
{
    _httpContextAccessor = httpContextAccessor;
    _controllerName = controllerName;
    _cosmosDbService = cosmosDbService;

    string logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    if (!Directory.Exists(logsDirectory)) Directory.CreateDirectory(logsDirectory);
    _logFilePath = Path.Combine(logsDirectory, "ApiLogs.txt");

    ExtractHttpContextData(); // Populate request data
    InitializeRateLimitRulesAsync().Wait(); // Load or create rules
}

    private async Task InitializeRateLimitRulesAsync()
    {
        try
        {
            var query = _cosmosDbService.RulesContainer.GetItemQueryIterator<RateLimitRule>("SELECT * FROM c");
            var results = new List<RateLimitRule>();
            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                results.AddRange(response);
            }

            if (results.Count == 0)
            {
                Console.WriteLine("No Rules Found in CosmosDB");
                results = GetDefaultRules();
                foreach (var rule in results)
                {
                    rule.id = Guid.NewGuid().ToString(); // Add ID for CosmosDB
                    await _cosmosDbService.RulesContainer.CreateItemAsync(rule, new PartitionKey(rule.id));
                    Console.WriteLine("DEFAULT RULES APPLIED");
                }
            }

            _rateLimitRules = results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Rule Init Error] {ex.Message}");
            _rateLimitRules = GetDefaultRules(); // Fallback to defaults in-memory
        }
    }

    private List<RateLimitRule> GetDefaultRules()
    {
        return new List<RateLimitRule>
        {
            new RateLimitRule { UserId = "Anonymous", IpAddress = "127.0.0.1", MaxRequests = 2, Type = "block",  BlockUntil = DateTime.UtcNow.AddSeconds(20) },
            new RateLimitRule { UserId = "Test person 1", IpAddress = "All", MaxRequests = 50, Type = "block", BlockUntil = DateTime.UtcNow.AddSeconds(20) },
            new RateLimitRule { UserId = "All", IpAddress = "All", MaxRequests = 1, Type = "allow", BlockUntil = DateTime.UtcNow.AddSeconds(0) }
        };
    }

    // Extracts user, IP, HTTP method, and path from HTTP context
    private void ExtractHttpContextData()
    {
        var context = _httpContextAccessor.HttpContext;

        _userId = context?.User?.Identity?.Name ?? "Anonymous";
        _ipAddress = context?.Connection?.RemoteIpAddress?.ToString() ?? "Unknown";

        if (_ipAddress == "::1")
        {
            _ipAddress = "127.0.0.1";
        }

        _httpMethod = context?.Request?.Method ?? "UNKNOWN";
        _path = context?.Request?.Path.Value ?? "/";
    }
    
    public class ApiLogResult
{
    public bool IsRequestAllowed { get; set; }
    public string BlockMessage { get; set; }

}
    // Starts API logging and checks rate limits
    public async Task<ApiLogResult> ApiLogStart()
{
    _startTime = DateTime.UtcNow;
    _stopwatch = Stopwatch.StartNew();
    string key = _userId + "_" + _ipAddress;

    // Fetch the rate-limiting rules from Cosmos DB
    var rateLimitRules = await _cosmosDbService.GetRateLimitRulesAsync(); // Fetch the rate limit rules from Cosmos DB

    // Initialize result
    var result = new ApiLogResult();

    if (rateLimitRules == null || !rateLimitRules.Any())
    {
        result.IsRequestAllowed = false;
        result.BlockMessage = "Rate limiting rules not found.";
        return result;
    }

    // Find the most applicable rule
    var matchingRule = rateLimitRules
        .Where(r =>
            (r.UserId == "All" || r.UserId == _userId) &&
            (r.IpAddress == "All" || r.IpAddress == _ipAddress))
        .OrderByDescending(r => (r.UserId != "All" ? 1 : 0) + (r.IpAddress != "All" ? 1 : 0)) // most specific first
        .FirstOrDefault();

    if (matchingRule == null)
    {
        result.IsRequestAllowed = false;
        result.BlockMessage = "No applicable rate limit rule found. Access denied.";
        return result;
    }

    // If the rule is of type 'allow', bypass rate limiting
    if (matchingRule.Type.Equals("allow", StringComparison.OrdinalIgnoreCase))
    {
        result.IsRequestAllowed = true;
        result.BlockMessage = string.Empty;
        return result;
    }

    // Check if user is currently blocked
    if (matchingRule.Type.Equals("block", StringComparison.OrdinalIgnoreCase))
    {
        if (DateTime.UtcNow < matchingRule.BlockUntil)
        {
            result.BlockMessage = $"User is temporarily blocked until {matchingRule.BlockUntil:O}";
            return result;
        }
    }

    // If the user is not blocked, continue to track their requests
    if (!RequestLog.TryGetValue(key, out var timestamps))
    {
        timestamps = new List<DateTime>();
        RequestLog[key] = timestamps;
    }

    // Remove old timestamps (older than 1 minute)
    timestamps.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-1));

    // Add the current timestamp to the log
    timestamps.Add(DateTime.UtcNow);

    // Check if the user exceeded the max requests allowed
    if (timestamps.Count > matchingRule.MaxRequests)
    {
        result.IsRequestAllowed = false;
        result.BlockMessage = "Too many requests. You are temporarily blocked for 20 seconds.";
        return result;
    }

    result.IsRequestAllowed = true;  // Request is allowed
    result.BlockMessage = string.Empty;
    return result;
}

public async Task<bool> SetRule(string userId, string ipAddress, int maxRequests, string type, int blockDurationSeconds = 0)
{
    try
    {
        var newRule = new RateLimitRule
        {
            id = Guid.NewGuid().ToString(),
            UserId = userId,
            IpAddress = ipAddress,
            MaxRequests = maxRequests,
            Type = type,
            BlockUntil = type.Equals("block", StringComparison.OrdinalIgnoreCase) 
                ? DateTime.UtcNow.AddSeconds(blockDurationSeconds) 
                : DateTime.UtcNow
        };

        await _cosmosDbService.RulesContainer.CreateItemAsync(newRule, new PartitionKey(newRule.id));

        Console.WriteLine($"[SetRule] Rule added: UserId={userId}, IP={ipAddress}, MaxRequests={maxRequests}, Type={type}, BlockUntil={newRule.BlockUntil}");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SetRule Error] {ex.Message}");
        return false;
    }
}

    // Stops the logging process, logs the data, and writes to storage
    public async Task ApiLogStop()
    {
        _stopwatch.Stop();
        var stopTime = DateTime.UtcNow;
        var totalTime = _stopwatch.Elapsed;

        var logEntry = new ApiLogEntry
        {
            id = Guid.NewGuid().ToString(),
            HttpMethod = _httpMethod,
            Path = _path,
            Controller = _controllerName,
            UserId = _userId,
            IpAddress = _ipAddress,
            StartTime = _startTime,
            StopTime = stopTime,
            DurationMs = totalTime.TotalMilliseconds
        };
        // Construct log message
        string logMessage = $"[START] {_httpMethod} {_path} | Controller: {_controllerName}, User: {_userId}, IP: {_ipAddress}, Time: {_startTime:O} | [STOP] {_httpMethod} {_path} | Controller: {_controllerName}, User: {_userId}, IP: {_ipAddress}, Time: {stopTime:O}, Duration: {totalTime.TotalMilliseconds} ms ;";

        Console.WriteLine(logMessage);
        LogToFile(logMessage);

        await SaveLogToCosmosAsync(logEntry);
    }

    // Writes log message to a local file
    private void LogToFile(string message, bool addNewLine = false)
    {
        try
        {
            var logLine = $"{DateTime.UtcNow:O} | {message}";
            if (addNewLine)
                logLine += Environment.NewLine;

            File.AppendAllText(_logFilePath, logLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Logger Error] Could not write to file: {ex.Message}");
        }
    }

    // Persists the log entry to Azure Cosmos DB
    private async Task SaveLogToCosmosAsync(ApiLogEntry logEntry)
    {
        try
        {
            await _cosmosDbService.LogContainer.CreateItemAsync(logEntry, new PartitionKey(logEntry.id));

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CosmosLog Error] {ex.Message}");
        }
    }

}

