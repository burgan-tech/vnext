using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Remote invoker for GetInstanceData tasks.
/// Supports both Dapr service invocation and direct HTTP calls.
/// Used for cross-domain instance data retrieval.
/// </summary>
public sealed class GetInstanceDataRemoteInvoker : ITaskInvoker<GetInstanceDataBinding>
{
    private readonly DaprClient _daprClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GetInstanceDataRemoteInvoker> _logger;
    private readonly ITaskMetrics _metrics;
    private readonly string _orchestrationAppId;

    public GetInstanceDataRemoteInvoker(
        DaprClient daprClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GetInstanceDataRemoteInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.GetInstanceData;

    /// <inheritdoc />
    public Type BindingType => typeof(GetInstanceDataBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<GetInstanceDataBinding> descriptor,
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
        var typedBinding = binding.Deserialize<GetInstanceDataBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize GetInstanceDataBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        GetInstanceDataBinding binding,
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
        GetInstanceDataBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var appId = binding.DaprAppId ?? _orchestrationAppId;
            var request = CreateDaprRequest(binding, appId);

            using var response = await _daprClient.InvokeMethodWithResponseAsync(request, cancellationToken);
            stopwatch.Stop();

            return await ProcessResponseAsync(binding, response, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning("GetInstanceData Dapr invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: "GetInstanceData remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstanceData Dapr invocation failed for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ExecuteWithHttpClientAsync(
        string? taskKey,
        GetInstanceDataBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var httpClient = CreateHttpClient(binding, taskKey);
            var request = CreateHttpRequest(binding);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            return await ProcessResponseAsync(binding, response, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning("GetInstanceData HTTP invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: "GetInstanceData remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstanceData HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstanceData HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}/{Instance}",
                taskKey, binding.Domain, binding.Workflow, binding.Instance);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ProcessResponseAsync(
        GetInstanceDataBinding binding,
        HttpResponseMessage response,
        long executionDurationMs,
        CancellationToken cancellationToken)
    {
        var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseData = InvokerHelpers.TryParseJson(content);
        var metadata = CreateMetadata(binding, reasonPhrase: response.ReasonPhrase);

        _metrics.RecordTaskExecution(TaskType, response.IsSuccessStatusCode ? "success" : "failure");

        return response.IsSuccessStatusCode
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

    private static string BuildPath(GetInstanceDataBinding binding)
    {
        var path = $"/api/v1/{binding.Domain}/workflows/{binding.Workflow}/instances/{binding.Instance}/data";

        if (binding.Extensions is { Length: > 0 })
        {
            var extensionsParam = string.Join("&", binding.Extensions.Where(e => !string.IsNullOrEmpty(e)).Select(e => $"extensions={Uri.EscapeDataString(e)}"));
            path += $"?{extensionsParam}";
        }

        return path;
    }

    private HttpRequestMessage CreateDaprRequest(GetInstanceDataBinding binding, string appId)
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

    private HttpRequestMessage CreateHttpRequest(GetInstanceDataBinding binding)
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
        GetInstanceDataBinding binding,
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

    private HttpClient CreateHttpClient(GetInstanceDataBinding binding, string? taskKey)
    {
        var clientName = binding.ValidateSSL
            ? WorkflowHttpClientNames.Default
            : WorkflowHttpClientNames.NoSslValidation;

        if (!binding.ValidateSSL)
        {
            _logger.LogWarning(
                "SSL certificate validation is disabled for {TaskType} task {TaskKey}",
                TaskType, taskKey);
        }

        var client = _httpClientFactory.CreateClient(clientName);
        client.Timeout = TimeSpan.FromSeconds(binding.TimeoutSeconds);
        
        return client;
    }
}
