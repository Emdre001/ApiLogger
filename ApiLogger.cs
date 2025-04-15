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
    private readonly Container _cosmosContainer;

    // In-memory request logs and block records.
    private static readonly Dictionary<string, List<DateTime>> RequestLog = new();
    private static readonly Dictionary<string, DateTime> BlockedUntil = new();
    
    // Defines a rule for rate limiting.
    public class RateLimitRule
    {
        public string UserId { get; set; } = "All";
        public string IpAddress { get; set; } = "All";
        public int MaxRequests { get; set; }
        public string Type { get; set; } = "block"; // or "allow"
    }

    // List of predefined rate limiting rules
    private static readonly List<RateLimitRule> RateLimitRules = new()
    {
        new RateLimitRule { UserId = "Anonymous", IpAddress = "Unknown", MaxRequests = 2, Type = "block" },
        new RateLimitRule { UserId = "Test person 1", IpAddress = "All", MaxRequests = 50, Type = "block" },
        new RateLimitRule { UserId = "All", IpAddress = "127.0.0.1", MaxRequests = 1, Type = "allow" }
    };

    // Constructor
    public ApiLogger(IHttpContextAccessor httpContextAccessor, string controllerName, Container cosmosContainer)
    {
        _httpContextAccessor = httpContextAccessor;
        _controllerName = controllerName;
        _cosmosContainer = cosmosContainer;

        // File log setup
        string logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        if (!Directory.Exists(logsDirectory)) Directory.CreateDirectory(logsDirectory);
        _logFilePath = Path.Combine(logsDirectory, "ApiLogs.txt");

        ExtractHttpContextData(); // Populate request data
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
     // Starts API logging and checks rate limits
    public bool ApiLogStart(out string blockMessage)
    {
        _startTime = DateTime.UtcNow;
        _stopwatch = Stopwatch.StartNew();
        string key = _userId + "_" + _ipAddress;
    
        // Find relevant rules
            var matchingRule = RateLimitRules
                .Where(r =>
                    (r.UserId == "All" || r.UserId == _userId) &&
                    (r.IpAddress == "All" || r.IpAddress == _ipAddress))
                .OrderByDescending(r => (r.UserId != "All" ? 1 : 0) + (r.IpAddress != "All" ? 1 : 0)) // most specific first
                .FirstOrDefault();

            // Fallback to default rule if none match
            if (matchingRule == null)
            {
                matchingRule = new RateLimitRule { MaxRequests = 10, Type = "block" }; 
            }

            // If rule is of type 'allow', bypass rate limiting
            if (matchingRule.Type.Equals("allow", StringComparison.OrdinalIgnoreCase))
            {
                blockMessage = string.Empty;
                return true;
            }

            // Check if user is currently blocked
            if (BlockedUntil.TryGetValue(key, out var blockedUntil))
            {
                if (blockedUntil > DateTime.UtcNow)
                {
                     blockMessage = $"User is temporarily blocked until {blockedUntil:O}";
            return false;
                }
                else
                {
                    BlockedUntil.Remove(key); // Delete the block after a certain time has passed 
                    RequestLog.Remove(key); // Clear the log also to start over after blocks
                }
            }
               if (!RequestLog.TryGetValue(key, out var timestamps))
            {
                timestamps = new List<DateTime>();
                RequestLog[key] = timestamps;
            }
            // Clear old calls older than 1 hour
            timestamps.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-1));
            // Add current call
            timestamps.Add(DateTime.UtcNow);
            // Checks if its's now more 5 within 1h
            if (timestamps.Count > matchingRule.MaxRequests)
            {
                BlockedUntil[key] = DateTime.UtcNow.AddSeconds(20);
              //  RequestLog[key] = new List<DateTime>();
                blockMessage = "Too many requests. You are temporarily blocked for 20 seconds.";
                return false;
            }

        blockMessage = string.Empty;
        return true;  
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
            await _cosmosContainer.CreateItemAsync(logEntry, new PartitionKey(logEntry.id));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CosmosLog Error] {ex.Message}");
        }
    }
}

