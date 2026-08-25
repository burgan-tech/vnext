using System.Diagnostics;
using BBT.Workflow.Execution.Invokers;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Registry for task invokers.
/// Routes task invocations to the appropriate invoker based on task type.
/// </summary>
public sealed class TaskInvokerRegistry : ITaskInvokerRegistry
{
    private readonly IReadOnlyDictionary<string, ITaskInvoker> _invokers;
    private readonly ILogger<TaskInvokerRegistry> _logger;

    public TaskInvokerRegistry(
        IEnumerable<ITaskInvoker> invokers,
        ILogger<TaskInvokerRegistry> logger)
    {
        _invokers = invokers.ToDictionary(
            i => i.TaskType,
            StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        
        _logger.LogInformation("TaskInvokerRegistry initialized with {Count} invokers: {TaskTypes}",
            _invokers.Count,
            string.Join(", ", _invokers.Keys));
    }

    /// <inheritdoc />
    public ITaskInvoker? GetInvoker(string taskType)
        => _invokers.GetValueOrDefault(taskType);

    /// <inheritdoc />
    public bool HasInvoker(string taskType)
        => _invokers.ContainsKey(taskType);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        if (!_invokers.TryGetValue(envelope.TaskType, out var invoker))
        {
            var error = $"No invoker registered for task type: {envelope.TaskType}";
            _logger.LogError(error);
            
            return TaskInvocationResult.Failure(error, metadata: new Dictionary<string, object>
            {
                ["RequestedTaskType"] = envelope.TaskType,
                ["AvailableTaskTypes"] = _invokers.Keys.ToArray()
            });
        }
        
        // One span per invocation, at the single place every task type passes through — including
        // a cache-aside miss calling back in for its source task, which is how that inner task
        // becomes visible without instrumenting each invoker.
        using var activity = InvokerActivityHelper.StartInvokeActivity(envelope.TaskType, envelope.TaskKey);

        try
        {
            var result = await invoker.InvokeAsync(
                envelope.TaskKey,
                envelope.Binding,
                cancellationToken);
            
            stopwatch.Stop();

            if (!result.IsSuccess)
                InvokerActivityHelper.SetError(activity, result.ErrorMessage);
            
            // Enrich result with timing if not already set
            if (result.ExecutionDurationMs == 0)
            {
                result = new TaskInvocationResult
                {
                    IsSuccess = result.IsSuccess,
                    StatusCode = result.StatusCode,
                    Body = result.Body,
                    Data = result.Data,
                    ErrorMessage = result.ErrorMessage,
                    Headers = result.Headers,
                    ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
                    TaskType = envelope.TaskType,
                    Metadata = result.Metadata
                };
            }
            
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            InvokerActivityHelper.SetError(activity, ex.Message);
            _logger.LogError(ex, "Task invocation failed for {TaskKey} of type {TaskType}",
                envelope.TaskKey, envelope.TaskType);

            return TaskInvocationResult.Failure(
                ex.Message,
                metadata: new Dictionary<string, object>
                {
                    ["TaskType"] = envelope.TaskType,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
    }
}

