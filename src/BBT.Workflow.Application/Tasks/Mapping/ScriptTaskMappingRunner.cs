using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Runs a binding's Roslyn <see cref="IMapping"/> script: <c>InputHandler(task, context)</c> before the task is invoked,
/// <c>OutputHandler(context).Data</c> after. This is the mapping block every executor used to carry a copy of, moved
/// here verbatim: the same compile path (the per-execution memo, <see cref="GetOrCompileMappingAsync{T}"/>), the same
/// <see cref="Error.Failure(string, string?, string?)"/> with <see cref="WorkflowErrorCodes.TaskExecution"/> and the
/// executor's message prefix in front of <see cref="ScriptDiagnostics.Explain"/>, and the same
/// <c>TaskInputHandlerFailed</c> / <c>TaskOutputHandlerFailed</c> events on the executor's logger
/// (<see cref="TaskMappingSite"/>). Stateless; one instance serves every execution.
/// </summary>
public sealed class ScriptTaskMappingRunner(IScriptEngine scriptEngine) : ITaskMappingRunner
{
    /// <inheritdoc />
    public async Task<Result<ScriptResponse?>> PrepareInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, CancellationToken ct, TaskMappingSite? site = null)
    {
        var mapping = ctx.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
            return Result<ScriptResponse?>.Ok(null);

        site ??= TaskMappingSite.For(task.GetTaskType());
        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async token =>
        {
            var scriptRunner = await CompileAsync(ctx, site, token);
            return await scriptRunner.InputHandler(task, ctx.ScriptContext);
        }, ct, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"{site.InputFailurePrefix}{ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess && site.LogsScriptInputFailures)
        {
            site.Logger?.TaskInputHandlerFailed(
                task.Key,
                site.TaskTypeName,
                ctx.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Result<object?>> ProcessOutputAsync(
        WorkflowTask task, TaskInvocationResult result, TaskExecutorContext ctx, CancellationToken ct,
        TaskMappingSite? site = null)
    {
        var mapping = ctx.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
            return Result<object?>.Ok(result.Data);

        site ??= TaskMappingSite.For(task.GetTaskType());
        var output = await ResultExtensions.TryAsync<object?>(async token =>
        {
            var scriptRunner = await CompileAsync(ctx, site, token);
            var outputResponse = await scriptRunner.OutputHandler(ctx.ScriptContext);
            return outputResponse.Data;
        }, ct, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"{site.OutputFailurePrefix}{ScriptDiagnostics.Explain(ex)}"));

        if (!output.IsSuccess && site.LogsScriptOutputFailures)
        {
            site.Logger?.TaskOutputHandlerFailed(
                task.Key,
                site.TaskTypeName,
                ctx.ScriptContext.Instance?.Id ?? Guid.Empty,
                output.Error.Message ?? "Unknown error");
        }

        return output;
    }

    private Task<IMapping> CompileAsync(TaskExecutorContext ctx, TaskMappingSite site, CancellationToken ct) =>
        site.MemoizesScriptCompile
            ? GetOrCompileMappingAsync<IMapping>(scriptEngine, ctx, ct)
            : scriptEngine.CompileToInstanceAsync<IMapping>(
                ctx.OnExecuteTask.Mapping!,
                flowScripts: ctx.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

    /// <summary>
    /// Compiles the task's mapping once per task execution and hands out a FRESH instance per call.
    /// The compiled factory is memoized on the <see cref="TaskExecutorContext"/> (keyed by mapping +
    /// target type), so PrepareInput and ProcessOutput/Invoke asking for the same mapping share a
    /// single engine call instead of each paying their own compile-cache lookup; instance-per-phase
    /// semantics are unchanged — a user script holding instance fields observes exactly today's
    /// behaviour, one fresh instance per phase.
    /// </summary>
    /// <remarks>
    /// No lock guards the memo dictionary: a single task execution runs its phases sequentially on
    /// one <see cref="TaskExecutorContext"/> within one thread (the pipeline does not fan phases of
    /// the SAME task out concurrently), so there is no concurrent writer to race against.
    /// CONSTRAINT: this helper compiles <c>context.OnExecuteTask.Mapping</c> and NOTHING else. A
    /// call site holding a different <see cref="ScriptCode"/> (e.g. the
    /// notification state-channel mapping or CacheAside's key script) must NOT route through here — it would silently compile
    /// the wrong script; keep such sites on the engine's <c>CompileToInstanceAsync</c> overloads.
    /// </remarks>
    public static async Task<T> GetOrCompileMappingAsync<T>(
        IScriptEngine scriptEngine,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        // Callers reach this only after a HasMappingCode guard; a binding with no script mapping is a caller bug.
        var mapping = context.OnExecuteTask.Mapping
            ?? throw new InvalidOperationException("The task binding has no script mapping to compile.");
        var key = (mapping, typeof(T));
        context.CompiledMappingFactories ??= new Dictionary<(ScriptCode, Type), object>();
        if (!context.CompiledMappingFactories.TryGetValue(key, out var boxed))
        {
            boxed = await scriptEngine.CompileToFactoryAsync<T>(
                mapping, context.ScriptContext.Workflow?.Scripts, cancellationToken);
            context.CompiledMappingFactories[key] = boxed;
        }
        else
        {
            // A memo hit means the engine is never called, so no Script.Compile span is produced at all.
            // Without this counter the trace shows no compile and no reuse — indistinguishable from a task
            // that has no mapping.
            Activity.Current.IncrementCounterTag(TelemetryConstants.TagNames.MappingFactoryMemoHits);
        }

        return ((Func<T>)boxed)();
    }
}
