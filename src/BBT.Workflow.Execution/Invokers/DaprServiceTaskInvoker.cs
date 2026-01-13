using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr service invocation task invoker - stateless execution with strongly-typed binding.
/// Receives prepared AppId, MethodName, HttpVerb, Headers and Body.
/// </summary>
public sealed class DaprServiceTaskInvoker(
    DaprClient daprClient,
    ILogger<DaprServiceTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<DaprServiceBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprService;

    /// <inheritdoc />
    public Type BindingType => typeof(DaprServiceBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprServiceBinding> descriptor,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(descriptor.TaskKey, descriptor.Binding, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<DaprServiceBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprServiceBinding");
        
        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprServiceBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = daprClient.CreateInvokeMethodRequest(
                new HttpMethod(binding.Method),
                binding.AppId,
                binding.MethodName);

            // Add query string
            if (!string.IsNullOrEmpty(binding.QueryString))
            {
                var uriBuilder = new UriBuilder(request.RequestUri!);
                var queryString = binding.QueryString.TrimStart('?');
                uriBuilder.Query = string.IsNullOrEmpty(uriBuilder.Query)
                    ? queryString
                    : uriBuilder.Query.TrimStart('?') + "&" + queryString;
                request.RequestUri = uriBuilder.Uri;
            }

            // Add body for non-GET requests
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                request.Content = new StringContent(binding.Body, Encoding.UTF8, "application/json");
            }

            // Add headers
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers.Where(h => h.Value != null))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Use InvokeMethodWithResponseAsync to get full HTTP response including status codes
            using var response = await daprClient.InvokeMethodWithResponseAsync(request, cancellationToken);

            var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var responseData = InvokerHelpers.TryParseJson(content);
            
            var metadata = new Dictionary<string, object>
            {
                ["AppId"] = binding.AppId,
                ["MethodName"] = binding.MethodName,
                ["HttpVerb"] = binding.Method,
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
            };

            // Record metrics based on success/failure
            _metrics.RecordDaprServiceInvocation(
                binding.AppId, 
                binding.MethodName, 
                response.IsSuccessStatusCode ? "success" : "failure");
            
            // Always return result with full response details - let output mapping handle error scenarios
            // All HTTP responses (2xx, 4xx, 5xx) include headers, body, and parsed data
            return response.IsSuccessStatusCode
                ? TaskInvocationResult.Success(
                    data: responseData,
                    body: content,
                    statusCode: (int)response.StatusCode,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    headers: responseHeaders,
                    metadata: metadata)
                : TaskInvocationResult.Failure(
                    error: $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    statusCode: (int)response.StatusCode,
                    body: content,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    headers: responseHeaders,
                    data: responseData,
                    metadata: metadata);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, "cancelled");
            logger.LogWarning("Dapr service invocation was cancelled: {AppId}/{MethodName}",
                binding.AppId, binding.MethodName);

            return TaskInvocationResult.Failure(
                error: "Dapr service invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["AppId"] = binding.AppId,
                    ["MethodName"] = binding.MethodName,
                    ["HttpVerb"] = binding.Method,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, "failure");
            logger.LogError(ex, "Unexpected error during Dapr service invocation: {AppId}/{MethodName}",
                binding.AppId, binding.MethodName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["AppId"] = binding.AppId,
                    ["MethodName"] = binding.MethodName,
                    ["HttpVerb"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }
}
