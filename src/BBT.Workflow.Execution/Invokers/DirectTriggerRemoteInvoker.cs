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
/// Uses Dapr service invocation to call the orchestration app's transition endpoint.
/// Used for cross-domain transition triggering on existing workflow instances.
/// Implements retry logic for instance lock scenarios (409 Conflict).
/// </summary>
public sealed class DirectTriggerRemoteInvoker : ITaskInvoker<DirectTriggerBinding>
{
    private readonly DaprClient _daprClient;
    private readonly ILogger<DirectTriggerRemoteInvoker> _logger;
    private readonly ITaskMetrics _metrics;
    private readonly string _orchestrationAppId;
    private readonly TriggerRetryOptions _retryOptions;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public DirectTriggerRemoteInvoker(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<DirectTriggerRemoteInvoker> logger,
        IOptions<TriggerRetryOptions>? retryOptions = null,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
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
        var stopwatch = Stopwatch.StartNew();
        
        if (string.IsNullOrEmpty(binding.Identifier))
        {
            stopwatch.Stop();
            return TaskInvocationResult.Failure(
                error: "DirectTrigger task requires either instanceId or key to be specified",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding, cancelled: true));
        }

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async token =>
                {
                    var httpRequest = CreateRequest(binding); // Create fresh request for each retry
                    return await _daprClient.InvokeMethodWithResponseAsync(httpRequest, token);
                },
                cancellationToken);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _metrics.RecordTaskExecution(TaskType, "failure");
                
                _logger.LogWarning(
                    "DirectTrigger failed after retries for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}. " +
                    "Status: {StatusCode}, Error: {Error}",
                    taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName,
                    (int)response.StatusCode, errorContent);

                return TaskInvocationResult.Failure(
                    error: $"HTTP {(int)response.StatusCode}: {errorContent}",
                    statusCode: (int)response.StatusCode,
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    errorCode: $"DirectTrigger:{(int)response.StatusCode}",
                    exceptionTypeName: "DirectTriggerRemoteException",
                    metadata: CreateMetadata(binding, statusCode: (int)response.StatusCode));
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var responseData = !string.IsNullOrEmpty(responseBody)
                ? JsonSerializer.Deserialize<object>(responseBody)
                : null;

            _metrics.RecordTaskExecution(TaskType, "success");

            return TaskInvocationResult.Success(
                data: responseData,
                body: responseBody,
                statusCode: (int)response.StatusCode,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: CreateMetadata(binding));
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            _logger.LogWarning(
                "DirectTrigger remote invocation was cancelled for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: "DirectTrigger remote invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                errorCode: "DirectTrigger.Cancelled",
                exceptionTypeName: ex.GetType().Name,
                metadata: CreateMetadata(binding, cancelled: true));
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "timeout");
            _logger.LogError(ex,
                "DirectTrigger remote invocation timeout for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                errorCode: "DirectTrigger.Timeout",
                exceptionTypeName: ex.GetType().Name,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskExecution(TaskType, "failure");
            _logger.LogError(ex,
                "DirectTrigger remote invocation failed for task {TaskKey}: {Domain}/{Workflow}/{InstanceId}/{TransitionKey}",
                taskKey, binding.Domain, binding.Workflow, binding.Identifier, binding.TransitionName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                errorCode: "DirectTrigger.Exception",
                exceptionTypeName: ex.GetType().Name,
                metadata: CreateMetadata(binding, exceptionType: ex.GetType().Name));
        }
    }

    private HttpRequestMessage CreateRequest(DirectTriggerBinding binding)
    {
        // Build the endpoint path
        var path = $"/api/v1/{binding.Domain}/workflows/{binding.Workflow}/instances/{binding.Identifier}/transitions/{binding.TransitionName}";

        // Add query parameters
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(binding.Version))
            queryParams.Add($"version={Uri.EscapeDataString(binding.Version)}");
        if (binding.Sync)
            queryParams.Add("sync=true");

        if (queryParams.Count > 0)
            path += "?" + string.Join("&", queryParams);

        // Build request body
        var requestBody = new
        {
            key = binding.Key,
            tags = binding.Tags,
            attributes = binding.Body
        };

        var request = _daprClient.CreateInvokeMethodRequest(
            HttpMethod.Patch,
            _orchestrationAppId,
            path);

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

        return request;
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
}