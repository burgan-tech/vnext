using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for FanOut tasks: resolves a collection at runtime and runs the referenced inner task
/// once per item, in parallel, then joins the per-item outcomes into ONE task result and ONE
/// instance-data write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parallelism is this executor's business; writing is the single writer's business.</strong>
/// Every item runs through the full task engine (retry, error boundary, journal, metrics) with
/// <see cref="TaskEngineExecutionOptions.SuppressDataApply"/>, on its own DI scope and its own
/// discarded branch <see cref="ScriptContext"/>. Nothing an item does reaches instance data; the
/// batch's single output — the mapping's <c>OutputHandler</c>, or the default packaging — is the
/// only thing that does.
/// </para>
/// <para>
/// The whole batch lives inside <c>InvokeAsync</c> rather than being split across the template
/// method's <c>Invoke</c>/<c>ProcessOutput</c> hooks: the join decides the task's success, and the
/// success flag belongs on the <see cref="TaskInvocationResult"/> that <c>InvokeAsync</c> returns.
/// Splitting it would mean smuggling the batch outcome between the two hooks.
/// </para>
/// </remarks>
public sealed class FanOutTaskExecutor : TaskExecutorBase<FanOutTask>
{
    private readonly IScriptEngine _scriptEngine;
    private readonly ITaskFactory _taskFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly FanOutConcurrencyLimiter _concurrencyLimiter;

    /// <summary>
    /// Initializes a new instance of <see cref="FanOutTaskExecutor"/>.
    /// </summary>
    public FanOutTaskExecutor(
        IScriptEngine scriptEngine,
        ITaskFactory taskFactory,
        IServiceScopeFactory serviceScopeFactory,
        FanOutConcurrencyLimiter concurrencyLimiter,
        ILogger<FanOutTaskExecutor> logger)
        : base(logger)
    {
        _scriptEngine = scriptEngine;
        _taskFactory = taskFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _concurrencyLimiter = concurrencyLimiter;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.FanOut;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        FanOutTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (task.ItemTask is null)
        {
            return ConfigurationFailure("FanOut task requires a 'task' reference for the inner item task.");
        }

        var mappingResult = await CompileMappingAsync(context, cancellationToken);
        if (!mappingResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(mappingResult.Error);
        }

        var mapping = mappingResult.Value;

        var itemsResult = await ResolveItemsAsync(task, mapping, context, cancellationToken);
        if (!itemsResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(itemsResult.Error);
        }

        var items = itemsResult.Value!;

        // The inner task template is loaded ONCE and cloned per item; loading it per item would
        // multiply component-store traffic by the batch size for no benefit.
        var templateResult = await _taskFactory.CreateExecutionTaskAsync(task.ItemTask, cancellationToken);
        if (!templateResult.IsSuccess)
        {
            return ConfigurationFailure(
                $"FanOut inner task '{task.ItemTask.Key}' could not be resolved: {templateResult.Error.Message}");
        }

        var template = templateResult.Value!;

        // Depth is limited to 1: a nested fan-out would deadlock against the global bulkhead —
        // the outer batch's items hold every slot while their inner items queue for one that
        // only an outer item can release.
        if (template.GetTaskType() == TaskType.FanOut)
        {
            return ConfigurationFailure(
                $"FanOut task '{task.Key}' cannot reference another FanOut task ('{template.Key}') as its item task: " +
                "nesting fan-out is not supported because nested batches deadlock against the global item bulkhead.");
        }

        // An empty batch is NOT short-circuited to success: it runs through the same join
        // evaluation as a non-empty one, and the threshold policies (quorum, firstSuccess) fail
        // it because zero successes cannot clear a threshold of at least one.
        var (itemResults, batchTimedOut) = items.Count == 0
            ? ([], false)
            : await RunBatchAsync(task, template, mapping, context, items, cancellationToken);

        var succeeded = itemResults.Count(r => r.IsSuccess);
        var fanOutResult = new FanOutResult(
            itemResults.Count,
            succeeded,
            itemResults.Count - succeeded,
            batchTimedOut,
            itemResults);

        var outputResult = await BuildOutputAsync(task, mapping, context, fanOutResult, cancellationToken);
        if (!outputResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(outputResult.Error);
        }

        var join = FanOutJoinEvaluator.Evaluate(task.JoinPolicy, task.MinSuccess, itemResults, batchTimedOut);
        stopwatch.Stop();

        if (join.IsSuccess)
        {
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: outputResult.Value,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType.ToString()));
        }

        // Deliberately NOT TaskInvocationResult.Failure(...): that factory carries no data, and a
        // failed all/quorum join must still land its result set in instance data so the error
        // boundary and auto-transitions can branch on which items failed.
        return Result<TaskInvocationResult>.Ok(new TaskInvocationResult
        {
            IsSuccess = false,
            Data = outputResult.Value,
            ErrorMessage = join.ErrorMessage,
            StatusCode = 500,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
            TaskType = TaskType.ToString()
        });
    }

    /// <summary>
    /// Compiles the task's <see cref="IFanOutMapping"/> once for the whole batch, or returns null
    /// when the task ships no mapping code (default item binding and default output packaging).
    /// </summary>
    private async Task<Result<IFanOutMapping?>> CompileMappingAsync(
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<IFanOutMapping?>.Ok(null);
        }

        return await ResultExtensions.TryAsync<IFanOutMapping?>(async ct =>
                await _scriptEngine.CompileToInstanceAsync<IFanOutMapping>(
                    mapping,
                    flowScripts: context.ScriptContext.Workflow?.Scripts,
                    cancellationToken: ct),
            cancellationToken,
            ex => Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"FanOut task mapping compilation failed: {ScriptDiagnostics.Explain(ex)}"));
    }

    /// <summary>
    /// Resolves the batch's items from exactly one of the two mutually exclusive sources:
    /// the task's <c>itemsPath</c> or the mapping's <c>ItemSelector</c>.
    /// </summary>
    private async Task<Result<IReadOnlyList<FanOutItem>>> ResolveItemsAsync(
        FanOutTask task,
        IFanOutMapping? mapping,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // The selector runs BEFORE the itemsPath branch, unconditionally, and that costs a
        // discarded script call on every itemsPath batch that also ships a mapping — the normal
        // case. It is deliberate: IFanOutMapping.ItemSelector has a default implementation
        // returning null, so there is no static way to ask whether the author overrode it, and
        // "returned null" is the only signal that distinguishes "no selector" from "empty
        // selection". The consequence to be aware of: on the ambiguous-config path below, the
        // author's selector has already run, side effects included, before the error is returned.
        // A selector is documented as a pure projection, so that is acceptable — but it is a
        // trade-off, not an oversight.
        var selectorResult = mapping is null
            ? Result<IEnumerable<dynamic>?>.Ok(null)
            : await ResultExtensions.TryAsync<IEnumerable<dynamic>?>(
                async _ => await mapping.ItemSelector(context.ScriptContext),
                cancellationToken,
                ex => Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"FanOut task item selector failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!selectorResult.IsSuccess)
        {
            return Result<IReadOnlyList<FanOutItem>>.Fail(selectorResult.Error);
        }

        var selected = selectorResult.Value;

        if (task.ItemsPath is { } itemsPath)
        {
            if (selected is not null)
            {
                return Result<IReadOnlyList<FanOutItem>>.Fail(Error.Validation(
                    WorkflowErrorCodes.TaskExecution,
                    $"FanOut task '{task.Key}' has an ambiguous item source: both 'itemsPath' " +
                    "and the mapping's ItemSelector produced a collection. Configure exactly one."));
            }

            // The RAW instance data, not ScriptContext.Instance.Data — the latter is already
            // converted to dynamic, and the path walk needs the JsonElement.
            var instanceData = context.ScriptContext.Instance?.LatestData?.Data.JsonElement;

            return ResultExtensions.Try<IReadOnlyList<FanOutItem>>(
                () => FanOutItemsResolver.Resolve(instanceData, itemsPath),
                ex => Error.Validation(WorkflowErrorCodes.TaskExecution, ex.Message));
        }

        if (selected is null)
        {
            return Result<IReadOnlyList<FanOutItem>>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                $"FanOut task '{task.Key}' has no item source: configure 'itemsPath', " +
                "or implement ItemSelector in the task's mapping."));
        }

        // Projected inside a guard, not just wrapped in Ok(...): the selector may hand back a lazy
        // sequence whose enumeration is where the script's work — and its exceptions — actually happen.
        return ResultExtensions.Try<IReadOnlyList<FanOutItem>>(
            () => FanOutItemsResolver.Project(selected),
            ex => Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"FanOut task item selector failed: {ScriptDiagnostics.Explain(ex)}"));
    }

    /// <summary>
    /// Runs every item under three bounds — the per-batch degree of parallelism, the process-wide
    /// bulkhead, and the batch/item deadlines — and returns the settled outcomes together with
    /// whether the batch as a whole hit its deadline.
    /// </summary>
    private async Task<(IReadOnlyList<FanOutItemResult> Items, bool TimedOut)> RunBatchAsync(
        FanOutTask task,
        WorkflowTask template,
        IFanOutMapping? mapping,
        TaskExecutorContext context,
        IReadOnlyList<FanOutItem> items,
        CancellationToken cancellationToken)
    {
        using var cancellation = FanOutBatchCancellation.Start(task, cancellationToken);
        using var degreeGate = new SemaphoreSlim(task.MaxDegreeOfParallelism, task.MaxDegreeOfParallelism);

        var settled = await Task.WhenAll(items.Select(item => ExecuteSingleItemAsync(
            task,
            template,
            mapping,
            context,
            item,
            degreeGate,
            cancellation)));

        // Results are ALWAYS returned in item-index order. The task's 'ordered' flag is accepted
        // by the schema for forward compatibility with durable mode (which may stream results in
        // completion order); in inline mode it has no observable effect, and it is deliberately
        // not read here rather than silently honoured in some other ordering.
        var ordered = settled.OrderBy(result => result.Index).ToList();

        // TimedOut is derived from what actually happened to items, NOT from the deadline CTS.
        // Reading the CTS is racy in a way no disarm can close: a timer firing in the instant
        // between the last item settling and the read would report a batch that finished in time
        // as timed out, and join policy 'all' would then fail a fully successful batch. An item
        // carries the batch-timeout code only if it was genuinely cut short by the deadline, so
        // early stop still yields false and a real timeout still yields true.
        var timedOut = ordered.Any(result => result.ErrorCode == FanOutErrorCodes.BatchTimeout);

        return (ordered, timedOut);
    }

    /// <summary>
    /// Executes one item: acquires its concurrency slots, applies the per-item deadline, runs the
    /// inner task, and classifies the outcome. Never throws except when the CALLER cancelled —
    /// every other fault becomes a failed <see cref="FanOutItemResult"/>, because one bad item
    /// must not take the batch's other outcomes down with it.
    /// </summary>
    private async Task<FanOutItemResult> ExecuteSingleItemAsync(
        FanOutTask task,
        WorkflowTask template,
        IFanOutMapping? mapping,
        TaskExecutorContext context,
        FanOutItem item,
        SemaphoreSlim degreeGate,
        FanOutBatchCancellation cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        var degreeSlotHeld = false;
        var globalSlotHeld = false;
        FanOutBatchCancellation.ItemWindow? window = null;
        FanOutItemResult settled;

        try
        {
            // Local gate first, then the global bulkhead: a batch can only ever hold
            // maxDegreeOfParallelism global slots, so one large batch cannot starve the process.
            await degreeGate.WaitAsync(cancellation.Token);
            degreeSlotHeld = true;
            await _concurrencyLimiter.WaitAsync(cancellation.Token);
            globalSlotHeld = true;

            // Opened only now that the slots are held: the per-item deadline measures execution,
            // not time spent queueing behind other items.
            window = cancellation.OpenItemWindow();

            settled = await RunItemAsync(task, template, mapping, context, item, window.Token, stopwatch);
        }
        catch (OperationCanceledException) when (cancellation.CallerCancelled)
        {
            // Tearing the transition down must propagate, not be reported as N failed items.
            // This clause exists because an exception filter only skips ITS OWN handler: without
            // it, a caller-cancelled OCE would fall through to the general catch below and be
            // recorded as an item failure, turning an aborted transition into a business-failure
            // result that the error boundary could then retry.
            throw;
        }
        catch (OperationCanceledException)
        {
            var (code, message) = cancellation.Classify(item, window);
            settled = new FanOutItemResult(item.Index, item.ItemKey, false, null, code, message, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            settled = new FanOutItemResult(
                item.Index, item.ItemKey, false, null, FanOutErrorCodes.ItemFailed,
                $"FanOut item {item.ItemKey} threw: {ScriptDiagnostics.Explain(ex)}", stopwatch.Elapsed);
        }
        finally
        {
            window?.Dispose();
            if (globalSlotHeld)
            {
                _concurrencyLimiter.Release();
            }

            if (degreeSlotHeld)
            {
                degreeGate.Release();
            }
        }

        cancellation.SignalEarlyStop(settled.IsSuccess);
        return settled;
    }

    /// <summary>
    /// Runs the inner task for one item on an isolated branch context and DI scope.
    /// </summary>
    /// <remarks>
    /// Takes the item's running stopwatch (<c>elapsed</c>) so it can stamp the duration itself and
    /// return a COMPLETE result, instead of returning a half-built one for the caller to finish.
    /// </remarks>
    private async Task<FanOutItemResult> RunItemAsync(
        FanOutTask task,
        WorkflowTask template,
        IFanOutMapping? mapping,
        TaskExecutorContext context,
        FanOutItem item,
        CancellationToken itemToken,
        Stopwatch elapsed)
    {
        // The branch is DISCARDED, never merged back. MergeParallelBranch would collide N item
        // responses on the single inner task key (MergeDictionary throws on a duplicate), and the
        // batch's write point is the output handler, not the items. It is also not disposed:
        // ScriptContext.Dispose clears the RelatedInstanceAccessor memo, which ForBranch SHARES
        // with the parent context — disposing a branch would evict the batch context's memo.
        var branch = context.ScriptContext.CreateParallelBranch();

        // Own DI scope per item, for the same reason TaskCoordinator gives its parallel task
        // group one: an isolated DbContext, since EF's change tracker is not thread-safe.
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ITaskExecutionEngine>();

        var itemTask = template.Clone();

        if (mapping is not null)
        {
            await mapping.ItemInputHandler(itemTask, branch, item);
        }
        else
        {
            branch.SetBody(item.Value);
        }

        var itemOnExecute = OnExecuteTask.Create(
            order: item.Index,
            task: task.ItemTask!,
            mapping: ScriptCode.FromNative(string.Empty),
            errorBoundary: task.ItemErrorBoundary);

        var options = new TaskEngineExecutionOptions
        {
            // Bound once, before the engine call. The engine's retry loop re-executes the same
            // prepared instance, which is the intended semantics here: retry THIS item with the
            // same input.
            PreparedTask = itemTask,
            SuppressDataApply = true,
            JournalTaskKey = $"{task.Key}#{item.Index}",
            CaptureResponse = true
        };

        var engineResult = await engine.ExecuteAsync(
            itemOnExecute,
            context.InstanceTransitionId,
            context.TaskTrigger,
            context.Origin,
            branch,
            options,
            itemToken);

        return MapEngineOutcome(item, engineResult, elapsed.Elapsed);
    }

    /// <summary>
    /// Maps one engine outcome onto a complete <see cref="FanOutItemResult"/>.
    /// </summary>
    private static FanOutItemResult MapEngineOutcome(
        FanOutItem item,
        Result<TasksExecutionResult> engineResult,
        TimeSpan duration)
    {
        if (!engineResult.IsSuccess)
        {
            return new FanOutItemResult(
                item.Index, item.ItemKey, false, null,
                engineResult.Error.Code ?? FanOutErrorCodes.ItemFailed,
                engineResult.Error.Message, duration);
        }

        var execution = engineResult.Value!;
        if (execution.IsSuccess)
        {
            return new FanOutItemResult(
                item.Index, item.ItemKey, true, execution.Response?.Data, null, null, duration);
        }

        // A business failure keeps its payload: under allSettled — the expected common policy — the
        // failed item's response body is exactly what a workflow author inspects to decide what to
        // do about it, and dropping it would leave them only a message.
        return new FanOutItemResult(
            item.Index, item.ItemKey, false, execution.Response?.Data,
            execution.TaskError?.NormalizedError.Code ?? FanOutErrorCodes.ItemFailed,
            execution.TaskError?.ErrorMessage
            ?? execution.Response?.ErrorMessage
            ?? $"FanOut item {item.ItemKey} failed.",
            duration);
    }

    /// <summary>
    /// Produces the batch's single output: the mapping's <c>OutputHandler</c> when one is
    /// configured, otherwise the default packaging.
    /// </summary>
    private static async Task<Result<object?>> BuildOutputAsync(
        FanOutTask task,
        IFanOutMapping? mapping,
        TaskExecutorContext context,
        FanOutResult result,
        CancellationToken cancellationToken)
    {
        if (mapping is null)
        {
            return Result<object?>.Ok(BuildDefaultOutput(task, result));
        }

        return await ResultExtensions.TryAsync<object?>(async _ =>
            {
                var response = await mapping.OutputHandler(context.ScriptContext, result);
                return response?.Data;
            },
            cancellationToken,
            ex => Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"FanOut task output handler failed: {ScriptDiagnostics.Explain(ex)}"));
    }

    /// <summary>
    /// The default output shape when the task ships no mapping: the per-item results under the
    /// configured result key, and the batch counters under <c>{resultKey}Summary</c>.
    /// </summary>
    private static object BuildDefaultOutput(FanOutTask task, FanOutResult result) =>
        new Dictionary<string, object?>
        {
            [task.ResultKey] = result.Items
                .Select(item => new Dictionary<string, object?>
                {
                    ["index"] = item.Index,
                    ["itemKey"] = item.ItemKey,
                    ["isSuccess"] = item.IsSuccess,
                    ["data"] = (object?)item.Data,
                    ["errorCode"] = item.ErrorCode,
                    ["errorMessage"] = item.ErrorMessage,
                    ["durationMs"] = (long)item.Duration.TotalMilliseconds
                })
                .ToList(),
            [$"{task.ResultKey}Summary"] = new Dictionary<string, object?>
            {
                ["total"] = result.Total,
                ["succeeded"] = result.Succeeded,
                ["failed"] = result.Failed,
                ["timedOut"] = result.TimedOut
            }
        };

    private static Result<TaskInvocationResult> ConfigurationFailure(string message) =>
        Result<TaskInvocationResult>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution, message));
}
