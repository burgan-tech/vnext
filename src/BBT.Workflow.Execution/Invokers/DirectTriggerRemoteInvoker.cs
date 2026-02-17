using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Remote invoker for DirectTrigger tasks.
/// Supports both Dapr service invocation and direct HTTP calls.
/// Used for cross-domain transition triggering on existing workflow instances.
/// Implements retry logic for instance lock scenarios (409 Conflict).
/// </summary>
public sealed class DirectTriggerRemoteInvoker : ITaskInvoker<DirectTriggerBinding>
{
    private readonly DaprClient _daprClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DirectTriggerRemoteInvoker> _logger;
    private readonly ITaskMetrics _metrics;
    private readonly string _orchestrationAppId;
    private readonly TriggerRetryOptions _retryOptions;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public DirectTriggerRemoteInvoker(
        DaprClient daprClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DirectTriggerRemoteInvoker> logger,
        IOptions<TriggerRetryOptions>? retryOptions = null,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
        _retryOptions = retryOptions?.Value ?? new TriggerRetryOptions();
        _retryPipeline = CreateRetryPipeline();
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.DirectTrigger;

    /// <inheritdoc />
    public Type BindingType => typeof(DirectTriggerBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DirectTriggerBinding> descriptor,
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
        var typedBinding = binding.Deserialize<DirectTriggerBinding>()
                           ?? throw new InvalidOperationException("Failed to deserialize DirectTriggerBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private ResiliencePipeline<HttpResponseMessage> CreateRetryPipeline()
    {
        var retryStatusCodes = _retryOptions.RetryOnStatusCodes
            .Select(code => (HttpStatusCode)code)
            .ToHashSet();

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = _retryOptions.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(_retryOptions.RetryDelayMilliseconds),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = _retryOptions.UseJitter,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response => retryStatusCodes.Contains(response.StatusCode)),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "DirectTrigger retry attempt {AttemptNumber}/{MaxAttempts} after {Delay}ms. " +
                        "Status: {StatusCode}. Reason: Instance is locked.",
                        args.AttemptNumber,
                        _retryOptions.MaxRetryAttempts,
                        args.RetryDelay.TotalMilliseconds,
                        (int)args.Outcome.Result!.StatusCode);
                    
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DirectTriggerBinding binding,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(binding.Identifier))
        {
            return TaskInvocationResult.Failure(
                error: "DirectTrigger task requires either instanceId or key to be specified",
                executionDurationMs: 0,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }

        // Route to Dapr or HttpClient based on binding
        if (binding.UseDapr && !string.IsNullOrEmpty(binding.DaprAppId))
        {
            return await ExecuteWithDaprAsync(taskKey, binding, cancellationToken);
        }

        return await ExecuteWithHttpClientAsync(taskKey, binding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteWithDaprAsync(
        string? taskKey,
        DirectTriggerBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var appId = binding.DaprAppId ?? _orchestrationAppId;

            var response = await _retryPipeline.ExecuteAsync(
                async token =>
                {
                    var httpRequest = CreateDaprRequest(binding, appId);
                    return await _daprClient.InvokeMethodWithResponseAsync(httpRequest, token);
                },
                cancellationToken);

            stopwatch.Stop();
            return await ProcessResponseAsync(taskKey, binding, response, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning(
                "DirectTrigger Dapr invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: "DirectTrigger remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex,
                "DirectTrigger Dapr invocation failed for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ExecuteWithHttpClientAsync(
        string? taskKey,
        DirectTriggerBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var httpClient = CreateHttpClient(binding, taskKey);

            var response = await _retryPipeline.ExecuteAsync(
                async token =>
                {
                    var httpRequest = CreateHttpRequest(binding);
                    return await httpClient.SendAsync(httpRequest, token);
                },
                cancellationToken);

            stopwatch.Stop();
            return await ProcessResponseAsync(taskKey, binding, response, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning(
                "DirectTrigger HTTP invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: "DirectTrigger remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex,
                "DirectTrigger HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

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
            _logger.LogError(ex,
                "DirectTrigger HTTP invocation failed for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private async Task<TaskInvocationResult> ProcessResponseAsync(
        string? taskKey,
        DirectTriggerBinding binding,
        HttpResponseMessage response,
        long executionDurationMs,
        CancellationToken cancellationToken)
    {
        var responseHeaders = InvokerHelpers.MergeHeaders(response.Headers, response.Content.Headers);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseData = InvokerHelpers.TryParseJson(content);
        var metadata = response.IsSuccessStatusCode
            ? CreateMetadata(binding)
            : CreateMetadata(binding, statusCode: (int)response.StatusCode);

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

    private static string BuildPath(DirectTriggerBinding binding)
    {
        var path = $"/api/v1/{binding.Domain}/workflows/{binding.Workflow}/instances/{binding.Identifier}/transitions/{binding.TransitionName}";

        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(binding.Version))
            queryParams.Add($"version={Uri.EscapeDataString(binding.Version)}");
        if (binding.Sync)
            queryParams.Add("sync=true");

        if (queryParams.Count > 0)
            path += "?" + string.Join("&", queryParams);

        return path;
    }

    private HttpRequestMessage CreateDaprRequest(DirectTriggerBinding binding, string appId)
    {
        var path = BuildPath(binding);

        var requestBody = new
        {
            key = binding.Key,
            tags = binding.Tags,
            attributes = binding.Body
        };

        var request = _daprClient.CreateInvokeMethodRequest(
            HttpMethod.Patch,
            appId,
            path);

        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        AddHeaders(request, binding.Headers);
        return request;
    }

    private HttpRequestMessage CreateHttpRequest(DirectTriggerBinding binding)
    {
        var path = BuildPath(binding);

        if (string.IsNullOrEmpty(binding.BaseUrl))
            throw new InvalidOperationException("BaseUrl is required for HTTP execution");

        var baseUrl = binding.BaseUrl.TrimEnd('/') + "/";
        var requestUri = new Uri(new Uri(baseUrl), path.TrimStart('/'));

        var requestBody = new
        {
            key = binding.Key,
            tags = binding.Tags,
            attributes = binding.Body
        };

        var request = new HttpRequestMessage(HttpMethod.Patch, requestUri)
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
        DirectTriggerBinding binding,
        bool cancelled = false,
        int? statusCode = null,
        string? exceptionType = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["Domain"] = binding.Domain,
            ["Workflow"] = binding.Workflow,
            ["InstanceId"] = binding.Identifier ?? string.Empty,
            ["TransitionKey"] = binding.TransitionName,
            ["OrchestrationAppId"] = _orchestrationAppId
        };

        if (cancelled)
            metadata["Cancelled"] = true;

        if (statusCode.HasValue)
            metadata["StatusCode"] = statusCode.Value;

        if (!string.IsNullOrEmpty(exceptionType))
            metadata["ExceptionType"] = exceptionType;

        return metadata;
    }

    private HttpClient CreateHttpClient(DirectTriggerBinding binding, string? taskKey)
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