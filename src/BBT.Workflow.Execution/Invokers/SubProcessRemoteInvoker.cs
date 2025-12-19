using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Remote invoker for SubProcess tasks.
/// Uses Dapr service invocation to call the orchestration app's subprocess start endpoint.
/// Used for cross-domain subprocess workflow instance creation.
/// Note: This invoker ONLY starts the subprocess. Correlation management is handled
/// by the orchestration layer (SubProcessTaskInvoker in Application).
/// </summary>
public sealed class SubProcessRemoteInvoker(
    DaprClient daprClient,
    IConfiguration configuration,
    ILogger<SubProcessRemoteInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<SubProcessBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;
    private readonly string _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";

    /// <inheritdoc />
    public string TaskType => TaskTypes.SubProcess;

    /// <inheritdoc />
    public Type BindingType => typeof(SubProcessBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<SubProcessBinding> descriptor,
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
        var typedBinding = binding.Deserialize<SubProcessBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize SubProcessBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        SubProcessBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Build the endpoint path: /api/v1/{domain}/workflows/sub/{workflow}/instances/start
            var path = $"/api/v1/{binding.Domain}/workflows/sub/{binding.Workflow}/instances/start";

            // Add query parameters
            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(binding.Version))
                queryParams.Add($"version={Uri.EscapeDataString(binding.Version)}");
            if (binding.Sync)
                queryParams.Add("sync=true");

            if (queryParams.Count > 0)
                path += "?" + string.Join("&", queryParams);

            // Build request body with subprocess-specific fields
            var requestBody = new
            {
                id = binding.InstanceId,
                key = binding.Key,
                tags = binding.Tags,
                attributes = binding.Body,
                callback = binding.Callback,
                extraProperties = binding.ExtraProperties
            };

            var request = daprClient.CreateInvokeMethodRequest(
                HttpMethod.Post,
                _orchestrationAppId,
                path);

            // Add body
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

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
                ["Domain"] = binding.Domain,
                ["Workflow"] = binding.Workflow,
                ["SubProcessInstanceId"] = binding.InstanceId.ToString(),
                ["OrchestrationAppId"] = _orchestrationAppId,
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
            };
            
            // Record metrics based on success/failure
            _metrics.RecordTaskExecution(TaskType, response.IsSuccessStatusCode ? "success" : "failure");

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
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            logger.LogWarning("SubProcess remote invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: "SubProcess remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Domain"] = binding.Domain,
                    ["Workflow"] = binding.Workflow,
                    ["SubProcessInstanceId"] = binding.InstanceId.ToString(),
                    ["OrchestrationAppId"] = _orchestrationAppId,
                    ["Cancelled"] = true
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError(ex, "SubProcess remote invocation failed for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Domain"] = binding.Domain,
                    ["Workflow"] = binding.Workflow,
                    ["SubProcessInstanceId"] = binding.InstanceId.ToString(),
                    ["OrchestrationAppId"] = _orchestrationAppId,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
    }
}

