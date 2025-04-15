
## 🗂️ API Logger & Rate Limiting

The `ApiLogger` class is a built-in utility that automatically logs incoming API requests and applies smart rate-limiting rules based on:
- **User Identity**
- **IP Address**
- **HTTP Method & Path**
- **Request Frequency**

### 🧾 Log Format

Each log includes:

- HTTP Method & Path  
- Controller Name  
- User ID & IP Address  
- Timestamps for Start/Stop  
- Duration in milliseconds  

Logs are written to:

- Local file: `/Logs/ApiLogs.txt`  
- Azure Cosmos DB (via injected container)

### ⛔ Rate Limiting Rules

Rate limits are matched by **UserId** and **IpAddress**. The most specific rule applies first. If none match, a default rule is used.
> ⏳ If a user exceeds their limit, they are blocked for 20 seconds.
