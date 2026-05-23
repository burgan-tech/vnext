# Scripting Engine

## Overview

The BBT Workflow Engine includes a scripting engine that compiles C# code into runtime instances using Roslyn. Scripts are compiled to strongly-typed interfaces and executed by the workflow runtime (mappings, conditions, timers, subflow mappings).

## Core Components

### 1. IScriptEngine Interface

The main interface provides compilation capabilities:

```csharp
public interface IScriptEngine : IScriptCompiler
{
}
```

### 2. Script Compiler (IScriptCompiler)

Provides capabilities for compiling C# code into executable instances:

```csharp
public interface IScriptCompiler
{
    Task<T> CompileToInstanceAsync<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default);
}
```

### 3. Evaluator and Script Services

- `IEvaluator` compiles script code and caches compiled types.
- `IScriptServices` provides Dapr, logging, and configuration to `ScriptBase` instances.

## Script Engine Implementation

### Core Features

- **Roslyn Integration**: Uses `CSharpCompilation` with C# 12
- **Caching**: Compiled script types are cached by `CSharpEvaluator`
- **ScriptBase Injection**: `IScriptServices` is injected into `ScriptBase` instances
- **Dapr Integration**: Secure access to secrets via `ScriptServices` + Dapr client
- **Automatic Assembly References**: Default .NET and workflow assemblies included
- **Metrics**: Compilation outcomes and duration are recorded via `IWorkflowMetrics`

**Implementation locations:**
- `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`
- `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs`
- `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Functions/ScriptBase.cs`

Embedded script delivery (used by notification tasks) is documented in
[Embedded Scripts and Dapr](../infrastructure/embedded-scripts-and-dapr.md).

### Default References and Imports

The engine automatically includes essential references and using statements:

```csharp
private static readonly MetadataReference[] DefaultReferences =
[
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(TimerSchedule).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(JsonSerializableAttribute).Assembly.Location)
];

private static readonly string[] DefaultUsings =
{
    "System",
    "System.IO",
    "System.Linq",
    "System.Collections.Generic",
    "System.Threading",
    "System.Threading.Tasks",
    "System.Dynamic",
    "System.Text.Json",
    "System.Text.Json.Serialization",
    "BBT.Workflow.Shared",
    "BBT.Workflow.Scripting",
    "BBT.Workflow.Definitions",
    "BBT.Workflow.Instances",
    "BBT.Workflow.Runtime",
    "BBT.Workflow.Scripting.Functions",
    "BBT.Workflow.Definitions.Timer"
};
```

## Script Interfaces and Contracts

### 1. IMapping Interface

Used for task input/output mapping:

```csharp
public interface IMapping
{
    Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context);
    Task<ScriptResponse> OutputHandler(ScriptContext context);
}
```

### 2. IConditionMapping Interface

For conditional logic evaluation:

```csharp
public interface IConditionMapping
{
    Task<bool> Handler(ScriptContext context);
}
```

### 3. ITimerMapping Interface

For flexible timer scheduling with Dapr-compatible functionality:

```csharp
public interface ITimerMapping
{
    Task<TimerSchedule> Handler(ScriptContext context);
}
```

### 4. Other Mapping Contracts

The scripting module also provides:

- `ITransitionMapping` for transition-level data mapping
- `ISubFlowMapping` and `ISubProcessMapping` for subflow/subprocess data exchange

The enhanced `ITimerMapping` interface supports:

- **DateTime scheduling**: `TimerSchedule.FromDateTime(dateTime)`
- **Cron expressions**: `TimerSchedule.FromCronExpression("0 9 * * *")`
- **Duration-based**: `TimerSchedule.FromDuration(TimeSpan.FromHours(2))`
- **Immediate execution**: `TimerSchedule.Immediate()`

**Example Timer Implementation:**

```csharp
public class PaymentDueTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var frequency = context.Instance.Data.paymentFrequency?.ToString() ?? "monthly";
        
        return frequency switch
        {
            "daily" => TimerSchedule.FromCronExpression("0 9 * * *"),
            "weekly" => TimerSchedule.FromCronExpression("0 9 * * 1"),
            "monthly" => TimerSchedule.FromCronExpression("0 9 1 * *"),
            "immediate" => TimerSchedule.Immediate(),
            _ => TimerSchedule.FromDuration(TimeSpan.FromDays(30))
        };
    }
}
```

## Script Context

The `ScriptContext` provides execution context to scripts:

```csharp
public class ScriptContext
{
    public dynamic? Body { get; private set; }
    public dynamic? Headers { get; private set; }
    public dynamic? RouteValues { get; private set; }
    public dynamic? QueryParameters { get; private set; }
    public Instance Instance { get; private set; }
    public Definitions.Workflow Workflow { get; private set; }
    public IRuntimeInfoProvider Runtime { get; private set; }
    public Transition Transition { get; private set; }
    public Dictionary<string, dynamic> Definitions { get; private set; }
    public Dictionary<string, dynamic?> TaskResponse { get; private set; }
    public Dictionary<string, dynamic?> OutputResponse { get; private set; }
    public Dictionary<string, dynamic> MetaData { get; private set; }

    public sealed class Builder
    {
        public Builder SetBody(object? body) { /* ... */ }
        public Builder SetHeaders(object? headers) { /* ... */ }
        public Builder SetRouteValues(object? routeValues) { /* ... */ }
        public Builder SetQueryParameters(object? queryParameters) { /* ... */ }
        public Builder SetWorkflow(Definitions.Workflow workflow) { /* ... */ }
        public Builder SetInstance(Instance instance) { /* ... */ }
        public Builder SetTransition(Transition? transition) { /* ... */ }
        public Builder SetRuntime(IRuntimeInfoProvider runtime) { /* ... */ }
        public Builder SetDefinitions(Dictionary<string, object> definitions) { /* ... */ }
        public Builder SetTaskResponse(Dictionary<string, object?> taskResponse) { /* ... */ }
        public Builder SetOutputResponse(Dictionary<string, object?> outputResponse) { /* ... */ }
        public Builder SetMetadata(Dictionary<string, object> metadata) { /* ... */ }
        public ScriptContext Build() { /* ... */ }
    }
}
```

### Script Context Factory

`IScriptContextFactory` builds `ScriptContext` instances using a fluent builder. The default `ScriptContextFactory` wires `IComponentCacheStore` and `IInstanceRepository` into `ScriptContextBuilder` to populate workflow and instance state.

## Global Script Functions

Scripts inherit from `ScriptBase` to access global helpers (secrets, logging, configuration, and dynamic helpers). These helpers rely on `IScriptServices`, injected after compilation.

### Usage Examples

**1. Using Global Functions:**
```csharp
var secretScript = @"
    using System.Threading.Tasks;
    using BBT.Workflow.Scripting;
    using BBT.Workflow.Definitions;
    using BBT.Workflow.Scripting.Functions;

    public class SecretMapping : ScriptBase, IMapping
    {
        public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
        {
            var apiKey = GetSecret(""dapr_store"", ""secret_store"", ""Asgard_ApiKey"");
            return Task.FromResult(new ScriptResponse { Data = new { apiKey } });
        }

        public Task<ScriptResponse> OutputHandler(ScriptContext context)
            => Task.FromResult(new ScriptResponse());
    }
";

var mapping = await scriptEngine.CompileToInstanceAsync<IMapping>(secretScript);
```

**2. Compiling Task Mapping:**
```csharp
var mappingCode = @"
    using System.Threading.Tasks;
    using BBT.Workflow.Scripting;
    using BBT.Workflow.Definitions;

    public class HttpTaskMapping : IMapping
    {
        public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
        {
            var httpTask = (task as HttpTask)!;
            
            // Set dynamic URL based on context
            httpTask.Url = ""https://api.example.com/customers/"" + context.Body?.customerId?.ToString();
            
            // Prepare request data
            var requestData = new
            {
                customerId = context.Body?.customerId?.ToString(),
                action = ""verification""
            };

            return Task.FromResult(new ScriptResponse
            {
                Data = requestData,
                Headers = new Dictionary<string, string>
                {
                    { ""Authorization"", ""Bearer "" + GetSecret(""dapr_store"", ""api_store"", ""auth_token"") }
                }
            });
        }

        public Task<ScriptResponse> OutputHandler(ScriptContext context)
        {
            // Transform response data
            var responseData = new
            {
                verified = context.Body?.verified ?? false,
                timestamp = DateTime.UtcNow,
                customerId = context.Body?.customerId?.ToString()
            };

            return Task.FromResult(new ScriptResponse
            {
                Data = responseData
            });
        }
    }
";

var mappingInstance = await scriptEngine.CompileToInstanceAsync<IMapping>(mappingCode);
```

## Script Execution Patterns

### 1. Task Mappings (IMapping)

Use `IMapping` for task input/output transformations and audit data:

```csharp
var mappingCode = @"
    using System.Threading.Tasks;
    using BBT.Workflow.Scripting;
    using BBT.Workflow.Definitions;

    public class CalculateInterest : IMapping
    {
        public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
            => Task.FromResult(new ScriptResponse());

        public Task<ScriptResponse> OutputHandler(ScriptContext context)
        {
            var principal = context.Body.loanAmount;
            var rate = context.Body.interestRate;
            var years = context.Body.termYears;

            var monthlyRate = rate / 12 / 100;
            var payments = years * 12;
            var monthlyPayment = principal * (monthlyRate * Math.Pow(1 + monthlyRate, payments)) /
                               (Math.Pow(1 + monthlyRate, payments) - 1);

            return Task.FromResult(new ScriptResponse
            {
                Data = new {
                    monthlyPayment = Math.Round(monthlyPayment, 2),
                    totalPayment = Math.Round(monthlyPayment * payments, 2)
                }
            });
        }
    }
";
```

### 2. Conditional Scripts

For workflow branching logic:

```csharp
var conditionCode = @"
    public class CreditCheckCondition : IConditionMapping
    {
        public Task<bool> Handler(ScriptContext context)
        {
            var creditScore = context.Body?.creditScore ?? 0;
            var loanAmount = context.Body?.loanAmount ?? 0;
            
            // Approve if credit score > 700 and loan amount < 100000
            var approved = creditScore > 700 && loanAmount < 100000;
            
            return Task.FromResult(approved);
        }
    }
";

var conditionInstance = await scriptEngine.CompileToInstanceAsync<IConditionMapping>(conditionCode);
var isApproved = await conditionInstance.Handler(scriptContext);
```

### 3. Advanced Data Processing

Using LINQ and complex transformations:

```csharp
var dataProcessingScript = @"
    using System.Threading.Tasks;
    using BBT.Workflow.Scripting;
    using BBT.Workflow.Definitions;

    public class CustomerFilter : IMapping
    {
        public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
            => Task.FromResult(new ScriptResponse());

        public Task<ScriptResponse> OutputHandler(ScriptContext context)
        {
            var customers = context.Body.customers.EnumerateArray()
                .Select(c => new {
                    Id = c.GetProperty(""id"").GetString(),
                    Name = c.GetProperty(""name"").GetString(),
                    Score = c.GetProperty(""creditScore"").GetInt32()
                })
                .Where(c => c.Score > 650)
                .OrderByDescending(c => c.Score)
                .Take(10)
                .ToList();

            return Task.FromResult(new ScriptResponse
            {
                Data = new {
                    eligibleCustomers = customers,
                    count = customers.Count,
                    averageScore = customers.Any() ? customers.Average(c => c.Score) : 0
                }
            });
        }
    }
";

var mapping = await scriptEngine.CompileToInstanceAsync<IMapping>(dataProcessingScript);
```

## Script Base Classes

### ScriptBase Class

`ScriptBase` provides convenient access to various global functions including secret management, logging, and configuration access. All scripts can inherit from this class to access these capabilities.

```csharp
public abstract class ScriptBase
{
    // Secret Management Functions
    protected string GetSecret(string storeName, string secretStore, string secretKey);
    protected async Task<string> GetSecretAsync(string storeName, string secretStore, string secretKey);
    protected Dictionary<string, string> GetSecrets(string storeName, string secretStore);
    protected async Task<Dictionary<string, string>> GetSecretsAsync(string storeName, string secretStore);
    
    // Logging Functions
    protected void LogTrace(string message);
    protected void LogDebug(string message);
    protected void LogInformation(string message);
    protected void LogWarning(string message);
    protected void LogError(string message);
    protected void LogCritical(string message);
    
    // Configuration Functions
    protected string? GetConfigValue(string key);
    protected string GetConfigValue(string key, string defaultValue);
    protected T? GetConfigValue<T>(string key);
    
    // Dynamic Object Helpers
    protected bool HasProperty(object obj, string propertyName);
    protected object? GetPropertyValue(object obj, string propertyName);
}
```

### Logging Functions

Scripts can now use structured logging functions to output diagnostic information:

```csharp
public class DiagnosticMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        LogDebug($"Starting task execution for {task.Key}");
        
        try
        {
            LogInformation($"Processing instance {context.Instance.Id}");
            
            var data = PrepareData(context);
            
            LogTrace($"Data prepared: {JsonSerializer.Serialize(data)}");
            
            return Task.FromResult(new ScriptResponse { Data = data });
        }
        catch (Exception ex)
        {
            LogError($"Task execution failed: {ex.Message}");
            throw;
        }
    }
}
```

**Log Levels:**
- `LogTrace`: Detailed debugging information (rarely used in production)
- `LogDebug`: Diagnostic information useful during development
- `LogInformation`: General information about workflow execution
- `LogWarning`: Warning messages for unexpected but recoverable situations
- `LogError`: Error messages for failures
- `LogCritical`: Critical failures requiring immediate attention

**Best Practices:**
- Use appropriate log levels based on importance
- Include contextual information (instance ID, task key, etc.)
- Avoid logging sensitive data (passwords, API keys, PII)
- Keep messages concise and meaningful
- Log performance-critical operations with timing information

### Configuration Functions

Scripts can access application configuration values at runtime:

```csharp
public class ConfigurableMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Get configuration values with different approaches
        var apiEndpoint = GetConfigValue("ExternalApi:Endpoint");
        var timeout = GetConfigValue("ExternalApi:Timeout", "30"); // with default
        var maxRetries = GetConfigValue<int>("ExternalApi:MaxRetries"); // typed
        
        LogInformation($"Using endpoint: {apiEndpoint}");
        
        var httpTask = (task as HttpTask)!;
        httpTask.Url = apiEndpoint;
        httpTask.Timeout = TimeSpan.FromSeconds(int.Parse(timeout));
        
        return Task.FromResult(new ScriptResponse 
        { 
            Data = new { maxRetries = maxRetries }
        });
    }
}
```

**Configuration Key Format:**
- Supports hierarchical keys with `:` separator (e.g., `"Section:SubSection:Key"`)
- Case-insensitive key matching
- Returns `null` if key not found (unless default value provided)

**Common Configuration Patterns:**
```csharp
// External service endpoints
var apiUrl = GetConfigValue("Services:PaymentApi:Url");

// Feature flags
var featureEnabled = GetConfigValue<bool>("Features:NewWorkflowEngine");

// Retry policies
var maxRetries = GetConfigValue<int>("Resilience:MaxRetries");
var backoffSeconds = GetConfigValue<double>("Resilience:BackoffSeconds");

// Business rules
var creditLimit = GetConfigValue<decimal>("BusinessRules:MaxCreditLimit");
```

### Dynamic Object Helper Functions

Work safely with dynamic objects and JSON data:

```csharp
public class DynamicDataMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var attributes = context.Body;
        
        // Check if properties exist before accessing
        if (HasProperty(attributes, "customerId"))
        {
            var customerId = GetPropertyValue(attributes, "customerId");
            LogInformation($"Processing customer: {customerId}");
        }
        
        // Safe navigation through nested objects
        if (HasProperty(attributes, "address") && 
            HasProperty(GetPropertyValue(attributes, "address"), "city"))
        {
            var city = GetPropertyValue(
                GetPropertyValue(attributes, "address"), 
                "city");
            LogDebug($"Customer city: {city}");
        }
        
        return Task.FromResult(new ScriptResponse { Data = "processed" });
    }
}
```

### Complete Example with All ScriptBase Features

```csharp
var scriptWithBase = @"
    using System;
    using System.Threading.Tasks;
    using System.Collections.Generic;
    
    public class AdvancedMapping : ScriptBase, IMapping
    {
        public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
        {
            // Log execution start
            LogInformation($""Starting task execution: {task.Key}"");
            
            try
            {
                // Get configuration values
                var apiEndpoint = GetConfigValue(""ExternalApi:Endpoint"");
                var timeout = GetConfigValue<int>(""ExternalApi:Timeout"");
                
                LogDebug($""Using endpoint: {apiEndpoint}, timeout: {timeout}s"");
                
                // Get secret for authentication
                var apiKey = GetSecret(""dapr_store"", ""secret_store"", ""api_key"");
                
                // Check dynamic properties safely
        if (HasProperty(context.Body, ""customerId""))
                {
            var customerId = GetPropertyValue(context.Body, ""customerId"");
                    LogTrace($""Processing for customer: {customerId}"");
                }
                
                var httpTask = (task as HttpTask)!;
                httpTask.Url = apiEndpoint;
                httpTask.Timeout = TimeSpan.FromSeconds(timeout);
                
                LogInformation(""Task prepared successfully"");
                
                return Task.FromResult(new ScriptResponse
                {
                    Data = new { status = ""prepared"" },
                    Headers = new Dictionary<string, string>
                    {
                        { ""Authorization"", ""Bearer "" + apiKey }
                    }
                });
            }
            catch (Exception ex)
            {
                LogError($""Task preparation failed: {ex.Message}"");
                throw;
            }
        }

        public Task<ScriptResponse> OutputHandler(ScriptContext context)
        {
            LogInformation(""Processing task output"");
            
            // Process response data
            var result = new
            {
                success = true,
                timestamp = DateTime.UtcNow,
                instanceId = context.Instance.Id
            };
            
            LogDebug($""Output processed: {result}"");
            
            return Task.FromResult(new ScriptResponse
            {
                Data = result,
                Headers = null
            });
        }
    }
";

var mappingInstance = await scriptEngine.CompileToInstanceAsync<IMapping>(scriptWithBase);
```

## Performance Optimization

### 1. Script Type Caching

`CSharpEvaluator` caches compiled types by code hash and reuses the same assembly for identical scripts:

```csharp
public int CachedTypeCount => _typeCache.Count;

// Uses a collectible AssemblyLoadContext per compiled script
// and injects IScriptServices into ScriptBase instances.
```

### 2. Default References and Usings

`ScriptEngine` merges caller-provided references/usings with cached defaults, avoiding repeated allocation and lookup.

## Error Handling

### Compilation Errors

```csharp
try
{
    var instance = await scriptEngine.CompileToInstanceAsync<IMapping>(code);
}
catch (InvalidOperationException ex)
{
    // Handle compilation errors (CSharpEvaluator throws InvalidOperationException)
    logger.LogError(ex, "Script compilation failed");
}
```

## Integration with Task Executors

Task executors compile the script to the appropriate interface and invoke handlers:

```csharp
var mapping = await scriptEngine.CompileToInstanceAsync<IMapping>(scriptCode, cancellationToken: cancellationToken);
var inputResponse = await mapping.InputHandler(task, context);
var outputResponse = await mapping.OutputHandler(context);
```

## Best Practices

### 1. Script Organization

- Keep scripts focused and single-purpose
- Use meaningful class and method names
- Include error handling in complex scripts

### 2. Performance Considerations

- Minimize heavy computations in scripts
- Use async methods for I/O operations
- Cache expensive lookups

### 3. Security Guidelines

- Always use DAPR secret store for sensitive data
- Validate input data in scripts
- Avoid hardcoded credentials

### 4. Testing Scripts

```csharp
[Test]
public async Task TestScriptExecution()
{
    var scriptCode = @"
        public class TestMapping : IMapping
        {
            public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
            {
                return Task.FromResult(new ScriptResponse { Data = ""test"" });
            }

            public Task<ScriptResponse> OutputHandler(ScriptContext context)
            {
                return Task.FromResult(new ScriptResponse { Data = ""output"" });
            }
        }
    ";

    var mapping = await scriptEngine.CompileToInstanceAsync<IMapping>(scriptCode);
    var result = await mapping.InputHandler(mockTask, mockContext);
    
    Assert.Equal("test", result.Data);
}
```

## Configuration

### Module Registration

Register script services, evaluator, and engine with DI:

```csharp
services.AddScoped<IScriptServices, ScriptServices>();
services.AddSingleton<IEvaluator, CSharpEvaluator>();
services.AddScoped<IScriptEngine, ScriptEngine>();
```

The scripting engine provides a powerful and flexible foundation for implementing dynamic business logic within workflows, enabling developers to create sophisticated workflow behaviors without requiring code recompilation or deployment.