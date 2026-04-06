using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Implementation of IRemoteInvokerService using Dapr service invocation.
/// Handles the communication with the Execution Service for remote task execution.
/// </summary>
public sealed class RemoteInvokerService : IRemoteInvokerService
{
    private readonly DaprClient _daprClient;
    private readonly string _executionServiceAppId;
    private readonly ILogger<RemoteInvokerService> _logger;

    /// <summary>
    /// Initializes a new instance of RemoteInvokerService.
    /// </summary>
    public RemoteInvokerService(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<RemoteInvokerService> logger)
    {
        _daprClient = daprClient;
        _executionServiceAppId = configuration["ExecutionApi:AppId"] ?? "vnext-execution";
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<TaskInvocationResult>> InvokeAsync(
        string taskType,
        string taskKey,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug("Invoking remote task {TaskKey} of type {TaskType} on {AppId}",
            taskKey, taskType, _executionServiceAppId);

        var request = new TaskInvokeRequest
        {
            Envelope = envelope,
            TraceContext = traceContext
        };

        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var httpRequest = _daprClient.CreateInvokeMethodRequest(
                _executionServiceAppId,
                $"/api/v1/execution/invoke/{taskType}/{taskKey}",
                request);

            httpRequest.Headers.Add(WorkflowInfo.Name, WorkflowInfo.Generate(
                traceContext.Domain ?? "unknown",
                traceContext.WorkflowKey ?? "unknown",
                traceContext.WorkflowVersion ?? "latest",
                traceContext.InstanceId));

            return await _daprClient.InvokeMethodAsync<TaskInvokeResponse>(httpRequest, ct);
        }, cancellationToken);

        stopwatch.Stop();

        if (!result.IsSuccess)
        {
            _logger.LogError("Failed to invoke remote task {TaskKey}: {Error}",
                taskKey, result.Error.Message);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: result.Error.Message ?? "Remote invocation failed",
                statusCode: 500,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType));
        }
        
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Remote task {TaskKey} execution failed: {Error}",
                taskKey, result.Error.Message ?? "Unknown error");

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: result.Error.Message ?? "Remote execution failed",
                statusCode: 500,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType,
                metadata: new Dictionary<string, object>
                {
                    ["RemoteSuccess"] = false,
                    ["TaskKey"] = taskKey
                }));
        }

        var response = result.Value!;
        
        // Update execution duration to include network time
        var remoteResult = new TaskInvocationResult
        {
            IsSuccess = response.Result!.IsSuccess,
            StatusCode = response.Result.StatusCode,
            Body = response.Result.Body,
            Data = response.Result.Data,
            ErrorMessage = response.Result.ErrorMessage,
            Headers = response.Result.Headers,
            TaskType = response.Result.TaskType,
            Metadata = response.Result.Metadata,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds
        };

        return Result<TaskInvocationResult>.Ok(remoteResult);
    }

    /// <inheritdoc />
    public TaskTraceContext CreateTraceContext(ScriptContext scriptContext)
    {
        var instance = scriptContext.Instance;
        var workflow = scriptContext.Workflow;
        var domain = !string.IsNullOrWhiteSpace(workflow?.Domain)
            ? workflow!.Domain
            : scriptContext.Runtime.Domain;

        return TaskTraceContext.Create(
            instanceId: instance?.Id ?? Guid.Empty,
            domain: domain,
            workflowKey: workflow?.Key ?? string.Empty,
            workflowVersion: workflow?.Version ?? string.Empty);
    }
}

