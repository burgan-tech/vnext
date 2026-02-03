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
/// Supports both Dapr service invocation and direct HTTP calls.
/// Used for cross-domain subprocess workflow instance creation.
/// Note: This invoker ONLY starts the subprocess. Correlation management is handled
/// by the orchestration layer (SubProcessTaskInvoker in Application).
/// </summary>
public sealed class SubProcessRemoteInvoker : ITaskInvoker<SubProcessBinding>
{
    private readonly DaprClient _daprClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubProcessRemoteInvoker> _logger;
    private readonly ITaskMetrics _metrics;
    private readonly string _orchestrationAppId;

    public SubProcessRemoteInvoker(
        DaprClient daprClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SubProcessRemoteInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
    }

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
        // Route to Dapr or HttpClient based on binding
        if (binding.UseDapr && !string.IsNullOrEmpty(binding.DaprAppId))
        {
            return await ExecuteWithDaprAsync(taskKey, binding, cancellationToken);
        }

        return await ExecuteWithHttpClientAsync(taskKey, binding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteWithDaprAsync(
        string? taskKey,
        SubProcessBinding binding,
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
            _logger.LogWarning("SubProcess Dapr invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: "SubProcess remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "SubProcess Dapr invocation failed for task {TaskKey}: {Domain}/{Workflow}",
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
        SubProcessBinding binding,
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
            _logger.LogWarning("SubProcess HTTP invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: "SubProcess remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex, "SubProcess HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}",
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
            _logger.LogError(ex, "SubProcess HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}",
                taskKey, binding.Domain, binding.Workflow);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ProcessResponseAsync(
        SubProcessBinding binding,
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

    private static string BuildPath(SubProcessBinding binding)
    {
        var path = $"/api/v1/{binding.Domain}/workflows/{binding.Workflow}/sub/instances/start";

        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(binding.Version))
            queryParams.Add($"version={Uri.EscapeDataString(binding.Version)}");
        if (binding.Sync)
            queryParams.Add("sync=true");
        else
            queryParams.Add("sync=false");

        queryParams.Add("strictIdempotency=true");

        if (queryParams.Count > 0)
            path += "?" + string.Join("&", queryParams);

        return path;
    }

    private static object CreateRequestBody(SubProcessBinding binding)
    {
        return new
        {
            id = binding.InstanceId,
            key = binding.Key,
            tags = binding.Tags,
            attributes = binding.Body,
            callback = binding.Callback,
            extraProperties = binding.ExtraProperties
        };
    }

    private HttpRequestMessage CreateDaprRequest(SubProcessBinding binding, string appId)
    {
        var path = BuildPath(binding);
        var requestBody = CreateRequestBody(binding);

        var request = _daprClient.CreateInvokeMethodRequest(
            HttpMethod.Post,
            appId,
            path);

        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        AddHeaders(request, binding.Headers);
        return request;
    }

    private HttpRequestMessage CreateHttpRequest(SubProcessBinding binding)
    {
        var path = BuildPath(binding);

        if (string.IsNullOrEmpty(binding.BaseUrl))
            throw new InvalidOperationException("BaseUrl is required for HTTP execution");

        var baseUrl = binding.BaseUrl.TrimEnd('/') + "/";
        var requestUri = new Uri(new Uri(baseUrl), path.TrimStart('/'));
        var requestBody = CreateRequestBody(binding);

        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json")
        };

        AddHeaders(request, binding.Headers);
        return request;
    }

    private static void AddHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson))
            return;

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        if (headers != null)
        {
            foreach (var header in headers.Where(h => h.Value != null))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    private Dictionary<string, object> CreateMetadata(
        SubProcessBinding binding,
        bool cancelled = false,
        string? reasonPhrase = null,
        string? exceptionType = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["Domain"] = binding.Domain,
            ["Workflow"] = binding.Workflow,
            ["SubProcessInstanceId"] = binding.InstanceId.ToString(),
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

    private HttpClient CreateHttpClient(SubProcessBinding binding, string? taskKey)
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
