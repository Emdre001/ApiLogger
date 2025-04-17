using System;

namespace Models;

// Defines a rule for rate limiting.
    public class RateLimitRule
{
    public string id { get; set; }
    public string UserId { get; set; }
    public string IpAddress { get; set; }
    public int MaxRequests { get; set; }
    public string Type { get; set; } // "block" or "allow"
    public int BlockDurationSeconds { get; set; } // NEW: duration instead of static timestamp
}
    