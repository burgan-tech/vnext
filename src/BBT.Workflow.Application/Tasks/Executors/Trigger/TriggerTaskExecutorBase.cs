using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
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
    /// Checks if the target domain matches the current runtime domain. An empty target domain means
    /// "this one".
    /// <para>
    /// PRIVATE on purpose: <see cref="RouteAsync{TResult}"/> is the only caller, so a subclass cannot
    /// branch on the domain itself and skip the child trace lane the local branch owes.
    /// </para>
    /// </summary>
    private bool IsSameDomain(TTask task)
    {
        var targetDomain = GetTargetDomain(task);
        if (string.IsNullOrEmpty(targetDomain))
            return true;

        return string.Equals(RuntimeInfoProvider.Domain, targetDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Routes the task to its local (same-domain, in-process) or remote (cross-domain, over Dapr)
    /// invocation, and — on the local branch only — opens a CHILD TRACE LANE anchored on this task's
    /// span.
    /// <para>
    /// The lane is why this helper exists rather than each executor branching for itself. A local
    /// dispatch runs in-process, so anything the target instance starts reads the ambient
    /// <see cref="WorkflowTraceLane"/> — which still belongs to the CALLING instance's request. Its
    /// transition jobs and post-commit work would then anchor to the caller's lane and surface as
    /// SIBLINGS of the caller's own hops, with nothing tying them to the task that triggered them.
    /// <see cref="WorkflowTraceLane.EnterChildLane"/> makes the current <c>Task.Execute</c> span the
    /// target instance's anchor instead, so the triggered work is flat UNDERNEATH the task, exactly
    /// as a subflow handoff already behaves.
    /// </para>
    /// <para>
    /// The remote branch needs nothing: it crosses Dapr, and the lane travels in the request as
    /// TraceRoot/ParentTraceRoot, already anchored by the invoker.
    /// </para>
    /// <para>
    /// For a read-only trigger task (GetInstance / GetInstances / GetInstanceData) the child lane is
    /// inert today — a read starts no lane-aware span. It is still entered, so the policy is
    /// uniform across the family and a read that later gains one cannot silently escape.
    /// </para>
    /// </summary>
    /// <typeparam name="TResult">The invocation result type; differs across the family.</typeparam>
    /// <param name="task">The task being routed.</param>
    /// <param name="local">Invoked when the target domain is this runtime's domain.</param>
    /// <param name="remote">Invoked when the target domain is another runtime's.</param>
    protected async Task<TResult> RouteAsync<TResult>(
        TTask task,
        Func<Task<TResult>> local,
        Func<Task<TResult>> remote)
    {
        var isSameDomain = IsSameDomain(task);

        Logger.TriggerTaskRouted(
            task.Key, TaskType.ToString(), GetTargetDomain(task) ?? string.Empty, isSameDomain);

        if (!isSameDomain)
            return await remote();

        using var lane = WorkflowTraceLane.EnterChildLane();
        return await local();
    }

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        TTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<ScriptResponse?>.Ok(null);
        }

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await ScriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Input handler failed for {TaskType}: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
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
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await ScriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Output handler failed for {TaskType}: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <summary>
    /// Maps an <see cref="Error"/> to the equivalent HTTP status code based on its prefix.
    /// Used to ensure local execution failures carry the same status codes as remote (Dapr) execution.
    /// </summary>
    protected static int MapErrorToStatusCode(Error error)
        => ErrorNormalizer.MapPrefixToStatusCode(error.Prefix) ?? 500;

    /// <summary>
    /// Converts task headers (JsonElement?) to Dictionary for local Input objects.
    /// </summary>
    protected static Dictionary<string, string?>? ConvertTaskHeadersToDictionary(JsonElement? taskHeaders)
    {
        if (!taskHeaders.HasValue || taskHeaders.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return taskHeaders.Value.Deserialize<Dictionary<string, string?>>();
        }
        catch
        {
            return null;
        }
    }
}

