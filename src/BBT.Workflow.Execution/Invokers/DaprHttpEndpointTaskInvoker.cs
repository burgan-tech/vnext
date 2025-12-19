using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr HTTP endpoint task invoker - stateless execution with strongly-typed binding.
/// Receives prepared EndpointName, Path, Method and Body.
/// </summary>
public sealed class DaprHttpEndpointTaskInvoker(
    DaprClient daprClient,
    ILogger<DaprHttpEndpointTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<DaprHttpEndpointBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprHttpEndpoint;

    /// <inheritdoc />
    public Type BindingType => typeof(DaprHttpEndpointBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprHttpEndpointBinding> descriptor,
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
        var typedBinding = binding.Deserialize<DaprHttpEndpointBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprHttpEndpointBinding");
        
        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprHttpEndpointBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = daprClient.CreateInvokeMethodRequest(
                new HttpMethod(binding.Method),
                binding.EndpointName,
                binding.Path);

            // Add body for non-GET requests
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                request.Content = new StringContent(binding.Body, Encoding.UTF8, "application/json");
            }

            // Use InvokeMethodWithResponseAsync to get full HTTP response including status codes
            using var response = await daprClient.InvokeMethodWithResponseAsync(request, cancellationToken);

            var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var responseData = InvokerHelpers.TryParseJson(content);
            
            var metadata = new Dictionary<string, object>
            {
                ["EndpointName"] = binding.EndpointName,
                ["Path"] = binding.Path,
                ["Method"] = binding.Method,
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
            };

            // Record metrics based on success/failure
            _metrics.RecordDaprServiceInvocation(
                binding.EndpointName,
                binding.Path,
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
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordDaprServiceInvocation(binding.EndpointName, binding.Path, "cancelled");
            logger.LogWarning("Dapr HTTP endpoint invocation was cancelled: {EndpointName}/{Path}",
                binding.EndpointName, binding.Path);

            return TaskInvocationResult.Failure(
                error: "Dapr HTTP endpoint invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["EndpointName"] = binding.EndpointName,
                    ["Path"] = binding.Path,
                    ["Cancelled"] = true
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordDaprServiceInvocation(binding.EndpointName, binding.Path, "failure");
            logger.LogError(ex, "Dapr HTTP endpoint invocation failed: {EndpointName}/{Path}",
                binding.EndpointName, binding.Path);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["EndpointName"] = binding.EndpointName,
                    ["Path"] = binding.Path,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }
}
