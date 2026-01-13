using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure HTTP task invoker - stateless execution with strongly-typed binding.
/// Receives prepared URL, headers, body and executes HTTP call.
/// </summary>
public sealed class HttpTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<HttpTaskBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <summary>
    /// Named HttpClient for SSL validation enabled requests (default behavior).
    /// </summary>
    public const string DefaultHttpClientName = "WorkflowHttpClient";

    /// <summary>
    /// Named HttpClient for SSL validation disabled requests.
    /// </summary>
    public const string NoSslValidationHttpClientName = "WorkflowHttpClient.NoSslValidation";

    /// <inheritdoc />
    public string TaskType => TaskTypes.Http;

    /// <inheritdoc />
    public Type BindingType => typeof(HttpTaskBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<HttpTaskBinding> descriptor,
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
        var typedBinding = binding.Deserialize<HttpTaskBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize HttpTaskBinding");
        
        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        HttpTaskBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var httpClient = CreateHttpClient(binding, taskKey);
            var request = new HttpRequestMessage(
                new HttpMethod(binding.Method),
                binding.Url);

            // Add headers
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Add body for non-GET requests
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                request.Content = new StringContent(
                    binding.Body,
                    Encoding.UTF8,
                    "application/json");
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var responseData = InvokerHelpers.TryParseJson(content);

            var metadata = new Dictionary<string, object>
            {
                ["Url"] = binding.Url,
                ["Method"] = binding.Method,
                ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
            };

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
                    error: $"HTTP {response.StatusCode}: {response.ReasonPhrase}",
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
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            
            logger.LogWarning("HTTP request was cancelled for task {TaskKey} - URL: {Url}", taskKey, binding.Url);
            
            return TaskInvocationResult.Failure(
                error: "HTTP request was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError(ex, "HTTP task invocation failed for {TaskKey} - URL: {Url}", taskKey, binding.Url);
            
            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError(ex, "Unexpected error during HTTP task invocation for {TaskKey}", taskKey);
            
            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["Url"] = binding.Url,
                    ["Method"] = binding.Method,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }

    private HttpClient CreateHttpClient(HttpTaskBinding binding, string? taskKey)
    {
        var clientName = binding.ValidateSSL
            ? DefaultHttpClientName
            : NoSslValidationHttpClientName;

        if (!binding.ValidateSSL)
        {
            logger.LogWarning("SSL certificate validation is disabled for HTTP task {TaskKey} - URL: {Url}",
                taskKey, binding.Url);
        }

        var client = httpClientFactory.CreateClient(clientName);
        client.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);
        return client;
    }

}
