using System.Diagnostics;
using System.Net.Http.Headers;
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
    // Kept local so the stateless Execution package does not acquire a Domain dependency.
    // These names are the public HTTP contract defined by TelemetryConstants in vNext Domain.
    private const string WorkflowInstanceHeader = "X-Workflow-Instance-Id";
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string SubHeader = "sub";
    private const string ActSubHeader = "act_sub";
    private const string WorkflowInstanceBaggage = "workflow.instance.id";
    private const string CorrelationBaggage = "correlation.id";
    private const string SubBaggage = "sub";
    private const string ActSubBaggage = "act.sub";

    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

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

            // Add headers. Content-Type is a content header in .NET, not a request header, so it is
            // captured here and applied to request.Content below instead of request.Headers.
            string? headerContentType = null;
            if (!string.IsNullOrEmpty(binding.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Headers);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            headerContentType = header.Value;
                            continue;
                        }

                        if (InvokerHelpers.IsReservedTraceHeader(header.Key))
                            continue;

                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            ApplyTrustedCorrelationHeaders(request);

            // Add body for non-GET requests. Resolution: explicit ContentType → Content-Type header → json.
            if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(binding.Body))
            {
                var contentType = HttpContentType.Resolve(binding.ContentType, headerContentType);
                request.Content = new StringContent(binding.Body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
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

            var isSuccess = response.IsSuccessStatusCode
                || AcceptedStatusCodeMatcher.IsAccepted((int)response.StatusCode, binding.AcceptedStatusCodes);

            // Always return result with full response details - let output mapping handle error scenarios
            // All HTTP responses (2xx, 4xx, 5xx) include headers, body, and parsed data
            return isSuccess
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
            ? WorkflowHttpClientNames.Default
            : WorkflowHttpClientNames.NoSslValidation;

        if (!binding.ValidateSSL)
        {
            logger.LogDebug("SSL certificate validation is disabled for HTTP task {TaskKey} - URL: {Url}",
                taskKey, binding.Url);
        }

        var client = httpClientFactory.CreateClient(clientName);
        client.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);
        return client;
    }

    private static void ApplyTrustedCorrelationHeaders(HttpRequestMessage request)
    {
        // Mapping-provided values are untrusted and must never be allowed to spoof
        // the workflow context established by vNext.
        request.Headers.Remove(WorkflowInstanceHeader);
        request.Headers.Remove(CorrelationHeader);
        request.Headers.Remove(SubHeader);
        request.Headers.Remove(ActSubHeader);

        var workflowInstance = Activity.Current?.GetBaggageItem(WorkflowInstanceBaggage);
        if (Guid.TryParse(workflowInstance, out var workflowInstanceId)
            && workflowInstanceId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(
                WorkflowInstanceHeader,
                workflowInstanceId.ToString("D").ToLowerInvariant());
        }

        var correlation = Activity.Current?.GetBaggageItem(CorrelationBaggage);
        if (Guid.TryParseExact(correlation, "N", out var correlationId)
            && correlationId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId.ToString("N"));
        }

        var subject = Activity.Current?.GetBaggageItem(SubBaggage);
        if (IsSafeIdentityClaim(subject))
        {
            request.Headers.TryAddWithoutValidation(SubHeader, subject);
        }

        var actSub = Activity.Current?.GetBaggageItem(ActSubBaggage);
        if (IsSafeIdentityClaim(actSub))
        {
            request.Headers.TryAddWithoutValidation(ActSubHeader, actSub);
        }
    }

    private static bool IsSafeIdentityClaim(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

}
