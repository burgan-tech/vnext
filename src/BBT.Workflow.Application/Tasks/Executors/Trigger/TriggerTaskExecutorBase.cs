using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Base class for trigger task executors that need domain-aware routing.
/// Provides common functionality for determining local vs remote execution.
/// </summary>
/// <typeparam name="TTask">The specific trigger task type.</typeparam>
public abstract class TriggerTaskExecutorBase<TTask>(
    IScriptEngine scriptEngine,
    IRuntimeInfoProvider runtimeInfoProvider,
    IRemoteInvokerService remoteInvoker,
    ILogger logger)
    : TaskExecutorBase<TTask>(logger)
    where TTask : WorkflowTask
{
    protected readonly IScriptEngine ScriptEngine = scriptEngine;
    protected readonly IRuntimeInfoProvider RuntimeInfoProvider = runtimeInfoProvider;
    protected readonly IRemoteInvokerService RemoteInvoker = remoteInvoker;

    /// <summary>
    /// Gets the target domain for the task.
    /// </summary>
    protected abstract string GetTargetDomain(TTask task);

    /// <summary>
    /// Checks if the target domain matches the current runtime domain.
    /// </summary>
    protected bool IsSameDomain(TTask task)
    {
        var targetDomain = GetTargetDomain(task);
        if (string.IsNullOrEmpty(targetDomain))
            return true;

        return string.Equals(RuntimeInfoProvider.Domain, targetDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result<ScriptResponse?>.Ok(null);
        }

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await ScriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Input handler failed for {TaskType}: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        TTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Update script context with response
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await ScriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Output handler failed for {TaskType}: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <summary>
    /// Extracts headers from the script context for HTTP calls.
    /// </summary>
    protected static Dictionary<string, string?>? ExtractHeaders(ScriptContext context)
    {
        if (context.Headers == null || context.Headers?.Count == 0)
            return null;

        var headers = new Dictionary<string, string?>();
        if (context.Headers != null)
            foreach (var header in context.Headers)
            {
                if (header.Value != null)
                {
                    headers[header.Key] = header.Value.ToString() ?? string.Empty;
                }
            }

        return headers.Count > 0 ? headers : null;
    }
}

