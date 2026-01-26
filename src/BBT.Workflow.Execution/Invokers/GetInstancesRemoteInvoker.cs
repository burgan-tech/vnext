using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Remote invoker for GetInstances tasks.
/// Supports both Dapr service invocation and direct HTTP calls.
/// Calls the data function endpoint to retrieve a list of instance data.
/// </summary>
public sealed class GetInstancesRemoteInvoker : ITaskInvoker<GetInstancesBinding>
{
    private readonly DaprClient _daprClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GetInstancesRemoteInvoker> _logger;
    private readonly ITaskMetrics _metrics;
    private readonly string _orchestrationAppId;

    public GetInstancesRemoteInvoker(
        DaprClient daprClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GetInstancesRemoteInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.GetInstances;

    /// <inheritdoc />
    public Type BindingType => typeof(GetInstancesBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<GetInstancesBinding> descriptor,
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
        var typedBinding = binding.Deserialize<GetInstancesBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize GetInstancesBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        GetInstancesBinding binding,
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
        GetInstancesBinding binding,
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
            _logger.LogWarning("GetInstances Dapr invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: "GetInstances remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstances Dapr invocation failed for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ExecuteWithHttpClientAsync(
        string? taskKey,
        GetInstancesBinding binding,
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
            _logger.LogWarning("GetInstances HTTP invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: "GetInstances remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "GetInstances HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

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
            _logger.LogError(ex, "GetInstances HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ProcessResponseAsync(
        GetInstancesBinding binding,
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

    private static string BuildPath(GetInstancesBinding binding)
    {
        // Build the path: /api/v1/{domain}/workflows/{workflow}/functions/data
        var path = $"/api/v1/{binding.Domain}/workflows/{binding.Workflow}/functions/data";

        var queryParams = new List<string>();

        if (binding.Page > 0)
            queryParams.Add($"page={binding.Page}");

        if (binding.PageSize > 0)
            queryParams.Add($"pageSize={binding.PageSize}");

        if (!string.IsNullOrEmpty(binding.Sort))
            queryParams.Add($"sort={Uri.EscapeDataString(binding.Sort)}");

        if (binding.Filter is { Length: > 0 })
        {
            foreach (var filter in binding.Filter.Where(f => !string.IsNullOrEmpty(f)))
            {
                queryParams.Add($"filter={Uri.EscapeDataString(filter)}");
            }
        }

        if (queryParams.Count > 0)
            path += "?" + string.Join("&", queryParams);

        return path;
    }

    private HttpRequestMessage CreateDaprRequest(GetInstancesBinding binding, string appId)
    {
        var path = BuildPath(binding);

        var request = _daprClient.CreateInvokeMethodRequest(
            HttpMethod.Get,
            appId,
            path);

        return request;
    }

    private HttpRequestMessage CreateHttpRequest(GetInstancesBinding binding)
    {
        var path = BuildPath(binding);

        if (string.IsNullOrEmpty(binding.BaseUrl))
            throw new InvalidOperationException("BaseUrl is required for HTTP execution");

        var baseUrl = binding.BaseUrl.TrimEnd('/') + "/";
        var requestUri = new Uri(new Uri(baseUrl), path.TrimStart('/'));

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        return request;
    }

    private Dictionary<string, object> CreateMetadata(
        GetInstancesBinding binding,
        bool cancelled = false,
        string? reasonPhrase = null,
        string? exceptionType = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["Domain"] = binding.Domain,
            ["Workflow"] = binding.Workflow,
            ["Page"] = binding.Page,
            ["PageSize"] = binding.PageSize,
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

    private HttpClient CreateHttpClient(GetInstancesBinding binding, string? taskKey)
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

        return _httpClientFactory.CreateClient(clientName);
    }
}
