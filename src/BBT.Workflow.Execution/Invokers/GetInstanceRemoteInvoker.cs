using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Services;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Remote invoker for GetInstance tasks.
/// Supports both Dapr service invocation and direct HTTP calls.
/// Used for cross-domain full instance (metadata + data) retrieval.
/// </summary>
public sealed class GetInstanceRemoteInvoker : ITaskInvoker<GetInstanceBinding>
{
    private readonly DaprClient _daprClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GetInstanceRemoteInvoker> _logger;
    private readonly ITaskMetrics _metrics;
    private readonly string _orchestrationAppId;

    public GetInstanceRemoteInvoker(
        DaprClient daprClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GetInstanceRemoteInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.GetInstance;

    /// <inheritdoc />
    public Type BindingType => typeof(GetInstanceBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<GetInstanceBinding> descriptor,
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
        var typedBinding = binding.Deserialize<GetInstanceBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize GetInstanceBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        GetInstanceBinding binding,
        CancellationToken cancellationToken)
    {
        // Route to Dapr or HttpClient based on binding
        if (binding.UseDapr && !string.IsNullOrEmpty(binding.DaprAppId))
        {
            return await ExecuteWithDaprAsync(taskKey, binding, cancellationToken);
        }

        return await ExecuteWithHttpClientAsync(taskKey, binding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteWithDaprAsync(
        string? taskKey,
        GetInstanceBinding binding,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var prepareActivity = InvokerActivityHelper.StartPrepareActivity(TaskType, taskKey ?? string.Empty);

        try
        {
            var appId = binding.DaprAppId ?? _orchestrationAppId;
            var request = CreateDaprRequest(binding, appId);

            prepareActivity?.Dispose();
            using var response = await _daprClient.InvokeMethodWithResponseAsync(request, cancellationToken);

            return await ProcessResponseAsync(binding, response, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            prepareActivity?.Dispose();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning("GetInstance Dapr invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: "GetInstance remote invocation was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (Exception ex)
        {
            prepareActivity?.Dispose();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstance Dapr invocation failed for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ExecuteWithHttpClientAsync(
        string? taskKey,
        GetInstanceBinding binding,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var prepareActivity = InvokerActivityHelper.StartPrepareActivity(TaskType, taskKey ?? string.Empty);

        try
        {
            var httpClient = CreateHttpClient(binding, taskKey);
            var request = CreateHttpRequest(binding);

            prepareActivity?.Dispose();
            using var response = await httpClient.SendAsync(request, cancellationToken);

            return await ProcessResponseAsync(binding, response, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            prepareActivity?.Dispose();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning("GetInstance HTTP invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: "GetInstance remote invocation was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (HttpRequestException ex)
        {
            prepareActivity?.Dispose();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstance HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
        catch (Exception ex)
        {
            prepareActivity?.Dispose();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstance HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ProcessResponseAsync(
        GetInstanceBinding binding,
        HttpResponseMessage response,
        long executionDurationMs,
        CancellationToken cancellationToken)
    {
        var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);
        var content = await response.ReadDecompressedContentAsync(cancellationToken);
        var responseData = InvokerHelpers.TryParseJson(content);
        var metadata = CreateMetadata(binding, reasonPhrase: response.ReasonPhrase);
        var isSuccess = response.IsSuccessStatusCode
            || AcceptedStatusCodeMatcher.IsAccepted((int)response.StatusCode, binding.AcceptedStatusCodes);

        _metrics.RecordTaskExecution(TaskType, isSuccess ? "success" : "failure");

        return isSuccess
            ? TaskInvocationResult.Success(
                data: responseData,
                body: content,
                statusCode: (int)response.StatusCode,
                executionDurationMs: executionDurationMs,
                taskType: TaskType,
                headers: responseHeaders,
                metadata: metadata)
            : TaskInvocationResult.Failure(
                error: $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                statusCode: (int)response.StatusCode,
                body: content,
                executionDurationMs: executionDurationMs,
                taskType: TaskType,
                headers: responseHeaders,
                data: responseData,
                metadata: metadata);
    }

    private static string BuildPath(GetInstanceBinding binding)
    {
        var domain = Uri.EscapeDataString(binding.Domain);
        var workflow = Uri.EscapeDataString(binding.Workflow);
        var instance = Uri.EscapeDataString(binding.Instance);
        var path = $"/api/v1/{domain}/workflows/{workflow}/instances/{instance}";

        if (binding.Extensions is { Length: > 0 })
        {
            var validExtensions = binding.Extensions
                .Where(e => !string.IsNullOrEmpty(e))
                .Select(e => $"extensions={Uri.EscapeDataString(e)}")
                .ToList();

            if (validExtensions.Count > 0)
            {
                path += $"?{string.Join("&", validExtensions)}";
            }
        }

        return path;
    }

    private HttpRequestMessage CreateDaprRequest(GetInstanceBinding binding, string appId)
    {
        var path = BuildPath(binding);

        var request = _daprClient.CreateInvokeMethodRequest(
            HttpMethod.Get,
            appId,
            path);

        if (!string.IsNullOrEmpty(binding.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", binding.ETag);
        }

        return request;
    }

    private HttpRequestMessage CreateHttpRequest(GetInstanceBinding binding)
    {
        var path = BuildPath(binding);

        if (string.IsNullOrEmpty(binding.BaseUrl))
            throw new InvalidOperationException("BaseUrl is required for HTTP execution");

        var baseUrl = binding.BaseUrl.TrimEnd('/') + "/";
        var requestUri = new Uri(new Uri(baseUrl), path.TrimStart('/'));

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        if (!string.IsNullOrEmpty(binding.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", binding.ETag);
        }

        return request;
    }

    private Dictionary<string, object> CreateMetadata(
        GetInstanceBinding binding,
        bool cancelled = false,
        string? reasonPhrase = null,
        string? exceptionType = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["Domain"] = binding.Domain,
            ["Workflow"] = binding.Workflow,
            ["Instance"] = binding.Instance,
            ["OrchestrationAppId"] = _orchestrationAppId
        };

        if (cancelled)
            metadata["Cancelled"] = true;

        if (!string.IsNullOrEmpty(reasonPhrase))
            metadata["ReasonPhrase"] = reasonPhrase;

        if (!string.IsNullOrEmpty(exceptionType))
            metadata["ExceptionType"] = exceptionType;

        return metadata;
    }

    private HttpClient CreateHttpClient(GetInstanceBinding binding, string? taskKey)
    {
        var clientName = binding.ValidateSSL
            ? WorkflowHttpClientNames.Default
            : WorkflowHttpClientNames.NoSslValidation;

        if (!binding.ValidateSSL)
        {
            _logger.LogDebug(
                "SSL certificate validation is disabled for {TaskType} task {TaskKey}",
                TaskType, taskKey);
        }

        var client = _httpClientFactory.CreateClient(clientName);
        client.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);

        return client;
    }
}
