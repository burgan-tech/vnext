# BBT Workflow Scripting Module

This module provides the necessary infrastructure to run dynamic C# scripts within the BBT Workflow system.

## Features

- **Dynamic C# Script Compilation**: Compile and run C# code at runtime
- **Custom Functions**: Special functions available for use within scripts
- **Dapr Integration**: Secure data access via Dapr secret store
- **Dependency Injection**: Full DI support with IScriptServices injection
- **Caching**: Caching to improve script compilation performance

## Architecture

Scripts inherit from `ScriptBase` and receive runtime services via property injection:

```
┌─────────────────────────────────────────────────────────────┐
│                    ScriptEngine                              │
│  (Gets IScriptServices from DI, passes to Evaluator)        │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                   CSharpEvaluator                            │
│  (Calls ScriptBase.SetServices() after compilation)         │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                     ScriptBase                               │
│  Accesses Logger, Dapr, Config via Services property        │
└─────────────────────────────────────────────────────────────┘
```

## Custom Functions

`ScriptBase` provides access to various helper functions including secret management, logging, configuration access, and dynamic object helpers.

### Using ScriptBase (Recommended)

Inherit from `ScriptBase` to access all helper functions. Services are automatically injected when the script is compiled:

```csharp
public class MyMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // All functions available via ScriptBase
        var apiKey = GetSecret("dapr_store", "secret_store", "Asgard_ApiKey");
        LogInformation("Processing customer request");
        var endpoint = GetConfigValue("Api:Endpoint");
        // ...
    }
}
```

### Secret Management Functions

#### `GetSecret(storeName, secretStore, secretKey)`

Retrieves a single secret value (synchronous).

```csharp
var apiKey = GetSecret("dapr_store", "secret_store", "Asgard_ApiKey");
```

#### `GetSecretAsync(storeName, secretStore, secretKey)`

Retrieves a single secret value (asynchronous).

```csharp
var apiKey = await GetSecretAsync("dapr_store", "secret_store", "Asgard_ApiKey");
```

#### `GetSecrets(storeName, secretStore)`

Retrieves bulk secret values (synchronous).

```csharp
var secrets = GetSecrets("dapr_store", "secret_store");
```

#### `GetSecretsAsync(storeName, secretStore)`

Retrieves bulk secret values (asynchronous).

```csharp
var secrets = await GetSecretsAsync("dapr_store", "secret_store");
```

### Logging Functions

Scripts can output diagnostic information using standard logging functions:

#### Function Signatures

```csharp
void LogTrace(string message)       // Detailed debugging information
void LogDebug(string message)       // Diagnostic information
void LogInformation(string message) // General information
void LogWarning(string message)     // Warning messages
void LogError(string message)       // Error messages
void LogCritical(string message)    // Critical failures
```

#### Usage Example

```csharp
public class DiagnosticMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        LogInformation($"Starting task: {task.Key}");
        
        try
        {
            LogDebug("Processing customer data");
            
            var result = ProcessData(context);
            
            LogInformation("Task completed successfully");
            
            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"Task failed: {ex.Message}");
            throw;
        }
    }
}
```

**Best Practices:**
- Use appropriate log levels (Debug for diagnostics, Information for business events)
- Include contextual information (task keys, instance IDs)
- Avoid logging sensitive data (passwords, API keys, PII)
- Keep messages concise and meaningful

### Configuration Functions

Scripts can access application configuration values at runtime:

#### Function Signatures

```csharp
string? GetConfigValue(string key)
string GetConfigValue(string key, string defaultValue)
T? GetConfigValue<T>(string key)
T GetConfigValue<T>(string key, T defaultValue)
```

#### Usage Example

```csharp
public class ConfigurableMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Get configuration with different approaches
        var apiUrl = GetConfigValue("ExternalApi:Endpoint");
        var timeout = GetConfigValue("ExternalApi:Timeout", "30"); // with default
        var maxRetries = GetConfigValue<int>("ExternalApi:MaxRetries"); // typed
        
        LogInformation($"Using API: {apiUrl}");
        
        var httpTask = (task as HttpTask)!;
        httpTask.Url = apiUrl;
        httpTask.Timeout = TimeSpan.FromSeconds(int.Parse(timeout));
        
        return Task.FromResult(new ScriptResponse 
        { 
            Data = new { maxRetries = maxRetries }
        });
    }
}
```

**Configuration Key Format:**
- Hierarchical keys with `:` separator (e.g., `"Section:SubSection:Key"`)
- Case-insensitive
- Returns `null` if not found (unless default provided)

### Dynamic Object Helper Functions

Work safely with dynamic objects and JSON data:

#### Function Signatures

```csharp
bool HasProperty(object obj, string propertyName)
object? GetPropertyValue(object obj, string propertyName)
T? GetPropertyValue<T>(object obj, string propertyName)
T GetPropertyValue<T>(object obj, string propertyName, T defaultValue)
```

#### Usage Example

```csharp
public class SafeDataMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Safely check and access dynamic properties
        if (HasProperty(context.Attributes, "customerId"))
        {
            var customerId = GetPropertyValue(context.Attributes, "customerId");
            LogInformation($"Processing customer: {customerId}");
        }
        
        // Get typed property with default
        var priority = GetPropertyValue(context.Attributes, "priority", 0);
        
        return Task.FromResult(new ScriptResponse { Data = "processed" });
    }
}
```

## Complete Usage Example

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class AdvancedMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Log execution start
        LogInformation($"Starting task: {task.Key}");
        
        try
        {
            // Get configuration
            var apiEndpoint = GetConfigValue("ExternalApi:Endpoint");
            var timeout = GetConfigValue<int>("ExternalApi:Timeout");
            
            LogDebug($"Using endpoint: {apiEndpoint}");
            
            // Get secret for authentication
            var apiKey = GetSecret("dapr_store", "secret_store", "Asgard_ApiKey");
            
            // Safe property access
            if (HasProperty(context.Attributes, "customerId"))
            {
                var customerId = GetPropertyValue(context.Attributes, "customerId");
                LogTrace($"Customer: {customerId}");
            }
            
            var httpTask = (task as HttpTask)!;
            httpTask.Url = apiEndpoint + "/" + context.Transition.Key;
            httpTask.Method = "POST";
            httpTask.Timeout = TimeSpan.FromSeconds(timeout);
            
            LogInformation("Task prepared successfully");
            
            return Task.FromResult(new ScriptResponse
            {
                Data = new { status = "prepared" },
                Headers = new Dictionary<string, string>
                {
                    {"Authorization", "Bearer " + apiKey}
                }
            });
        }
        catch (Exception ex)
        {
            LogError($"Task preparation failed: {ex.Message}");
            throw;
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("Processing output");
        
        var result = new
        {
            success = true,
            timestamp = DateTime.UtcNow
        };
        
        LogDebug($"Output: {result}");
        
        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Headers = null
        });
    }
}
```

## Dependency Injection

The scripting module uses dependency injection for runtime services:

### IScriptServices Interface

```csharp
public interface IScriptServices
{
    DaprClient DaprClient { get; }
    ILogger Logger { get; }
    IConfiguration Configuration { get; }
}
```

### Registration

Services are registered in `TaskServiceCollectionExtensions.AddScriptingServices()`:

```csharp
services.TryAddScoped<IScriptServices, ScriptServices>();
services.TryAddSingleton<IEvaluator, CSharpEvaluator>();
services.TryAddScoped<IScriptEngine, ScriptEngine>();
```

## Configuration

### Secret Store Configuration

The Dapr secret store component is defined in the `etc/dapr/components/secretstore.yaml` file:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: workflow-secretstore
spec:
  type: secretstores.hashicorp.vault
  version: v1
  metadata:
    - name: vaultAddr
      value: http://vnext-vault:8200
    - name: vaultToken
      value: "admin"
    # ... other configurations
```

## Security

- All secret accesses are performed via Dapr
- Secret values are not stored in the script cache
- Secret values are not logged in case of errors
- Services are scoped per-request for proper isolation

## Performance

- Scripts are cached after the initial compilation
- Compiled types are reused across requests
- Async functions are recommended for I/O-bound operations
- Secret accesses involve network calls and should be used carefully

## Error Handling

- If services are not available, appropriate exceptions are thrown
- `ArgumentException` is thrown for invalid parameter values
- Detailed error messages are provided for secret access errors
- Logging gracefully degrades if logger is unavailable
