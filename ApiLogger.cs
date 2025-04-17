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
    private static Dictionary<string, DateTime> UserBlockStatus = new();

    
    // Constructor
    public ApiLogger(IHttpContextAccessor httpContextAccessor, string controllerName, CosmosDbService cosmosDbService, bool skipDefaultInit = false)
    {
        _httpContextAccessor = httpContextAccessor;
        _controllerName = controllerName;
        _cosmosDbService = cosmosDbService;

        string logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        if (!Directory.Exists(logsDirectory)) Directory.CreateDirectory(logsDirectory);
        _logFilePath = Path.Combine(logsDirectory, "ApiLogs.txt");

        ExtractHttpContextData(); // Populate request data
        if (!skipDefaultInit)
    {
        Task.Run(() => InitializeRateLimitRulesAsync()).Wait(); // eller .GetAwaiter().GetResult();
    }
    }

    private async Task InitializeRateLimitRulesAsync(bool skipDefaults = false)
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
            if (results.Count == 0 && !skipDefaults)
            {
                Console.WriteLine("No Rules Found in CosmosDB");
                results = GetDefaultRules();
                foreach (var rule in results)
                {
                    rule.id = Guid.NewGuid().ToString();
                    await _cosmosDbService.RulesContainer.CreateItemAsync(rule, new PartitionKey(rule.id));
                    Console.WriteLine("DEFAULT RULES APPLIED");
                }
            }

            _rateLimitRules = results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Rule Init Error] {ex.Message}");
            _rateLimitRules = GetDefaultRules();
        }
    }

    private List<RateLimitRule> GetDefaultRules()
    {
        return new List<RateLimitRule>
        {
            new RateLimitRule { UserId = "Anonymous", IpAddress = "All", MaxRequests = 5, Type = "block",  BlockDurationSeconds= 20 },
            new RateLimitRule { UserId = "Test person 1", IpAddress = "All", MaxRequests = 50, Type = "block", BlockDurationSeconds= 20 },
            new RateLimitRule { UserId = "All", IpAddress = "All", MaxRequests = 3, Type = "allow", BlockDurationSeconds= 20 }
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

    var result = new ApiLogResult();

    // Get rate limit rules from Cosmos DB
    var rateLimitRules = await _cosmosDbService.GetRateLimitRulesAsync();

    if (rateLimitRules == null || !rateLimitRules.Any())
    {
        Console.WriteLine("------ERROR: No rate limit rules found------");
        result.IsRequestAllowed = false;
        result.BlockMessage = "Rate limiting rules not found.";
        return result;
    }

    // Find the most specific matching rule
    var matchingRule = rateLimitRules
        .Where(r =>
            (r.UserId == "All" || r.UserId == _userId) &&
            (r.IpAddress == "All" || r.IpAddress == _ipAddress))
        .OrderByDescending(r => (r.UserId != "All" ? 1 : 0) + (r.IpAddress != "All" ? 1 : 0))
        .FirstOrDefault();

    if (matchingRule == null)
    {
        Console.WriteLine("------ERROR: No matching rule found------");
        result.IsRequestAllowed = false;
        result.BlockMessage = "No applicable rate limit rule found.";
        return result;
    }

    // Allow rules skip all checks
    if (matchingRule.Type.Equals("allow", StringComparison.OrdinalIgnoreCase))
    {
        result.IsRequestAllowed = true;
        result.BlockMessage = string.Empty;
        return result;
    }

    // Check if user is currently blocked
    if (UserBlockStatus.TryGetValue(key, out DateTime blockedUntil))
    {
        if (DateTime.UtcNow < blockedUntil)
        {
            result.IsRequestAllowed = false;
            result.BlockMessage = $"You are currently blocked until {blockedUntil:O}";
            return result;
        }
        else
        {
            // Unblock user and reset tracking
            UserBlockStatus.Remove(key);
            RequestLog.Remove(key); // Clear request history after block expires
        }
    }

    // Track requests per minute
    if (!RequestLog.TryGetValue(key, out var timestamps))
    {
        timestamps = new List<DateTime>();
        RequestLog[key] = timestamps;
    }

    timestamps.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-1));
    timestamps.Add(DateTime.UtcNow);

    if (timestamps.Count > matchingRule.MaxRequests)
    {
        // Calculate dynamic block duration from rule's BlockUntil value
        var blockDuration = matchingRule.BlockDurationSeconds > 0 ? matchingRule.BlockDurationSeconds : 20;
        var blockUntil = DateTime.UtcNow.AddSeconds(blockDuration);
        UserBlockStatus[key] = blockUntil;

        UserBlockStatus[key] = blockUntil;

        result.IsRequestAllowed = false;
        result.BlockMessage = $"Too many requests. You are blocked until {blockUntil:O}";
        return result;
    }

    result.IsRequestAllowed = true;
    result.BlockMessage = string.Empty;
    return result;
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

    public async Task<bool> SetRule(string userId, string ipAddress, int maxRequests, string type, int blockDurationSeconds)
{
    try
    {
        await InitializeRateLimitRulesAsync(skipDefaults: true);
        var newRule = new RateLimitRule
        {
            id = Guid.NewGuid().ToString(),
            UserId = userId,
            IpAddress = ipAddress,
            MaxRequests = maxRequests,
            Type = type,
            BlockDurationSeconds = blockDurationSeconds
        };

        await _cosmosDbService.RulesContainer.CreateItemAsync(newRule, new PartitionKey(newRule.id));

        Console.WriteLine($"[SetRule] Rule added: UserId={userId}, IP={ipAddress}, MaxRequests={maxRequests}, Type={type}, BlockDuration={blockDurationSeconds}");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SetRule Error] {ex.Message}");
        return false;
    }
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

