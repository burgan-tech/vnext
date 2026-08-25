using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
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
    private readonly IWorkflowMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of <see cref="FanOutTaskExecutor"/>.
    /// </summary>
    public FanOutTaskExecutor(
        IScriptEngine scriptEngine,
        ITaskFactory taskFactory,
        IServiceScopeFactory serviceScopeFactory,
        FanOutConcurrencyLimiter concurrencyLimiter,
        IWorkflowMetrics metrics,
        ILogger<FanOutTaskExecutor> logger)
        : base(logger, metrics)
    {
        _scriptEngine = scriptEngine;
        _taskFactory = taskFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _concurrencyLimiter = concurrencyLimiter;
        _metrics = metrics;
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

        Logger.FanOutBatchStarted(
            task.Key,
            items.Count,
            AliasOf(task),
            task.MaxDegreeOfParallelism,
            task.JoinPolicy.ToString(),
            InstanceIdOf(context));

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

        // Batch shape on the ambient Task.Invoke span: with one span per item the tree already
        // shows WHICH item was slow, but not how big the batch was or how much of it failed —
        // and that is the first thing asked about a batch whose items are individually fine.
        var batchActivity = Activity.Current;
        batchActivity?.SetTag(TelemetryConstants.TagNames.FanOutItemCount, itemResults.Count);
        batchActivity?.SetTag(TelemetryConstants.TagNames.FanOutSucceededCount, succeeded);
        batchActivity?.SetTag(TelemetryConstants.TagNames.FanOutFailedCount, itemResults.Count - succeeded);
        batchActivity?.SetTag(TelemetryConstants.TagNames.FanOutTimedOut, batchTimedOut);

        // Recorded HERE — as soon as the batch has settled and its counters are final — rather
        // than on the return paths below. The output handler is author code that can fail, and a
        // batch that ran must still be counted exactly once whatever the handler does with it.
        ObserveBatchOutcome(task, context, fanOutResult, stopwatch.Elapsed);

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

        return await ResultExtensions.TryAsync<IFanOutMapping?>(
            async ct => await GetOrCompileMappingAsync<IFanOutMapping>(_scriptEngine, context, ct),
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
        var saturationLatch = new OnceLatch();

        var settled = await Task.WhenAll(items.Select(item => ExecuteSingleItemAsync(
            task,
            template,
            mapping,
            context,
            item,
            degreeGate,
            cancellation,
            saturationLatch)));

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
        FanOutBatchCancellation cancellation,
        OnceLatch saturationLatch)
    {
        var stopwatch = Stopwatch.StartNew();
        var degreeSlotHeld = false;
        var globalSlotHeld = false;
        FanOutBatchCancellation.ItemWindow? window = null;
        FanOutItemResult settled;

        // One span per item, opened BEFORE the slot waits so the trace separates "queued behind
        // the bulkhead" from "the item itself is slow" — the first question an operator asks about
        // a slow batch. It is also what stops the N items from fighting over one ambient span:
        // TaskExecutionEngine renames Activity.Current in place, so without a span of its own each
        // item would rename the batch's.
        using var activity = TaskExecutionActivityHelper.StartActivity(
            TaskExecutionActivityHelper.OperationFanOutItem, task.Key, TaskType.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.FanOutItemKey, item.ItemKey);
        activity?.SetTag(TelemetryConstants.TagNames.FanOutItemIndex, item.Index);
        activity?.SetTag(TelemetryConstants.TagNames.FanOutItemAlias, AliasOf(task));

        try
        {
            // Local gate first, then the global bulkhead: a batch can only ever hold
            // maxDegreeOfParallelism global slots, so one large batch cannot starve the process.
            await degreeGate.WaitAsync(cancellation.Token);
            degreeSlotHeld = true;
            await AcquireGlobalSlotAsync(task, saturationLatch, cancellation.Token);
            globalSlotHeld = true;

            activity?.SetTag(
                TelemetryConstants.TagNames.FanOutItemQueueWaitMs,
                (long)stopwatch.Elapsed.TotalMilliseconds);

            // Opened only now that the slots are held: the per-item deadline measures execution,
            // not time spent queueing behind other items.
            window = cancellation.OpenItemWindow();

            settled = await RunItemAsync(task, template, mapping, context, item, cancellation, window, stopwatch);
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

        // Early stop stays FIRST, ahead of the reporting: it is the only statement here that
        // affects the other items in flight, and observation must not stand between a decided
        // verdict and the siblings it cancels.
        cancellation.SignalEarlyStop(settled.IsSuccess);

        ObserveItemOutcome(task, context, item, settled, activity);
        return settled;
    }

    /// <summary>
    /// Takes one process-wide bulkhead slot, reporting the FIRST time this batch has to queue for
    /// one.
    /// </summary>
    /// <remarks>
    /// Contention is detected from the returned task's <see cref="Task.IsCompleted"/>:
    /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> hands back an already-completed
    /// task when it could take the count synchronously, and a real waiter otherwise. That is a
    /// pure observation of the wait that is happening anyway — unlike a <c>Wait(0)</c> fast path,
    /// it takes no second acquisition attempt, so it cannot barge ahead of already-queued waiters
    /// and cannot leak a slot if the log call throws.
    /// <para>
    /// The latch keeps this to once per batch: with a batch far larger than the bulkhead, every
    /// item after the first N would report the same saturation, which is volume, not information.
    /// </para>
    /// </remarks>
    private async Task AcquireGlobalSlotAsync(
        FanOutTask task,
        OnceLatch saturationLatch,
        CancellationToken cancellationToken)
    {
        var slot = _concurrencyLimiter.WaitAsync(cancellationToken);

        if (!slot.IsCompleted && saturationLatch.TryFire())
        {
            Logger.FanOutBulkheadSaturated(task.Key, _concurrencyLimiter.ActiveCount, _concurrencyLimiter.Capacity);
        }

        await slot;
    }

    /// <summary>
    /// Reports one settled item: names it in the log when it failed, and finishes its span.
    /// </summary>
    private void ObserveItemOutcome(
        FanOutTask task,
        TaskExecutorContext context,
        FanOutItem item,
        FanOutItemResult settled,
        Activity? activity)
    {
        // The inner execution renamed this span (TaskExecutionEngine sets the display name of
        // Activity.Current rather than starting its own), so the fan-out identity is re-asserted
        // here — a trace of N identically named siblings does not tell an operator which item
        // is the straggler.
        if (activity is not null)
        {
            activity.DisplayName =
                $"{TaskExecutionActivityHelper.OperationFanOutItem}[{item.Index}] {item.ItemKey}";
        }

        if (settled.IsSuccess)
        {
            return;
        }

        var errorCode = settled.ErrorCode ?? FanOutErrorCodes.ItemFailed;
        Logger.FanOutItemFailed(
            task.Key,
            item.ItemKey,
            item.Index,
            errorCode,
            settled.ErrorMessage,
            InstanceIdOf(context));
        TaskExecutionActivityHelper.SetError(activity, settled.ErrorMessage, errorCode);
    }

    /// <summary>
    /// Reports one settled batch: its counters, its duration, and — when the deadline fired — how
    /// much of it had settled on its own before being cut short.
    /// </summary>
    private void ObserveBatchOutcome(
        FanOutTask task,
        TaskExecutorContext context,
        FanOutResult result,
        TimeSpan elapsed)
    {
        var instanceId = InstanceIdOf(context);

        if (result.TimedOut)
        {
            var settledCount = result.Items.Count(item => item.ErrorCode != FanOutErrorCodes.BatchTimeout);
            Logger.FanOutBatchTimedOut(
                task.Key, settledCount, result.Total, task.BatchTimeoutSeconds, instanceId);
        }

        Logger.FanOutBatchCompleted(
            task.Key,
            result.Total,
            result.Succeeded,
            result.Failed,
            (long)elapsed.TotalMilliseconds,
            instanceId);

        _metrics.RecordFanOutBatch(
            task.Key,
            context.ScriptContext.Workflow?.Key ?? UnknownWorkflow,
            result.Total,
            result.Succeeded,
            result.Failed,
            elapsed.TotalSeconds);
    }

    private static Guid InstanceIdOf(TaskExecutorContext context) =>
        context.ScriptContext.Instance?.Id ?? Guid.Empty;

    /// <summary>
    /// The task's readability label for one item, or <see cref="DefaultItemAlias"/> when it
    /// declares none.
    /// </summary>
    /// <remarks>
    /// <c>ItemAlias</c> is a REPORTING label and nothing else — resolving it here, at the two places
    /// that report, is what keeps it that way. It must never reach the item's input binding, which
    /// stays a flat <c>SetBody(item.Value)</c>: giving the alias a role in binding would change the
    /// shape every inner-task script sees, keyed on a field authors set for readability.
    /// <para>
    /// Whitespace counts as absent, so an author who wrote <c>"itemAlias": ""</c> gets a log line
    /// that still reads as a sentence rather than <c>Items=12 ''</c>.
    /// </para>
    /// </remarks>
    private static string AliasOf(FanOutTask task) =>
        string.IsNullOrWhiteSpace(task.ItemAlias) ? DefaultItemAlias : task.ItemAlias;

    /// <summary>
    /// Neutral stand-in for an unset <c>itemAlias</c>. Lives here rather than on the definition so
    /// an authored <c>"item"</c> stays distinguishable from an absent alias.
    /// </summary>
    private const string DefaultItemAlias = "item";

    /// <summary>
    /// Metric label used when a fan-out task runs outside a flow (function/extension origin), where
    /// there is no workflow to attribute the batch to. A literal keeps the label cardinality closed
    /// instead of emitting an empty series.
    /// </summary>
    private const string UnknownWorkflow = "unknown";

    /// <summary>
    /// One-shot, thread-safe latch. Exists so a condition observed by many items in flight is
    /// reported once per batch rather than once per item.
    /// </summary>
    private sealed class OnceLatch
    {
        private int _fired;

        public bool TryFire() => Interlocked.Exchange(ref _fired, 1) == 0;
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
        FanOutBatchCancellation cancellation,
        FanOutBatchCancellation.ItemWindow window,
        Stopwatch elapsed)
    {
        // The branch is DISCARDED, never merged back. MergeParallelBranch would collide N item
        // responses on the single inner task key (MergeDictionary throws on a duplicate), and the
        // batch's write point is the output handler, not the items. Branch creation is copy-on-
        // write (Body shared until the item's first write, dictionaries container-copied), so the
        // per-item cost is small. It is also not disposed: the RelatedInstanceAccessor memo is
        // SHARED with the batch context via ForBranch — branch Dispose is owned-parts-only now
        // and leaves that memo alone, but not disposing keeps the batch's memo independent of
        // Dispose's gating.
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
            window.Token);

        return MapEngineOutcome(item, engineResult, elapsed.Elapsed, cancellation, window);
    }

    /// <summary>
    /// Maps one engine outcome onto a complete <see cref="FanOutItemResult"/>, re-attributing to the
    /// batch's own cancellation causes the outcomes that only LOOK like the inner task's failures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the re-attribution exists — do not "simplify" it away.</strong> The task engine
    /// observes an item's cancellation before the fan-out layer ever can: its catch-all absorbs the
    /// inner <c>TaskCanceledException</c> and hands back
    /// <c>Task:Unknown:{itemTaskKey}:TaskCanceledException</c>. Accepted verbatim, that made a single
    /// batch report two different codes for one cause — an item cancelled while still queueing got
    /// the documented <see cref="FanOutErrorCodes.ItemCancelled"/>, while its sibling cancelled a
    /// moment later, already inside the engine, got the exception name. The leaked string is not part
    /// of the fan-out contract and even embeds the inner task's key, so it is not stable to match on;
    /// <see cref="FanOutErrorCodes"/> values ARE contract and authors branch on them.
    /// </para>
    /// <para>
    /// The decision asks the cancellation context — its tokens are the truth about whether WE stopped
    /// this item — rather than pattern-matching the error text. The two failure shapes are then
    /// treated differently on purpose: an engine that did not complete was interrupted, so our
    /// cancellation explains it; an engine that COMPLETED and reported a task failure produced the
    /// item's own outcome, which keeps its own code even while the batch is stopping, unless the
    /// failure is itself explicitly cancellation-typed.
    /// </para>
    /// </remarks>
    private static FanOutItemResult MapEngineOutcome(
        FanOutItem item,
        Result<TasksExecutionResult> engineResult,
        TimeSpan duration,
        FanOutBatchCancellation cancellation,
        FanOutBatchCancellation.ItemWindow window)
    {
        var stoppedByBatch = cancellation.StoppedItem(window);

        if (!engineResult.IsSuccess)
        {
            // The engine unwound instead of completing. If the CALLER is what cancelled, the
            // transition is being torn down and that must escape the executor — the engine having
            // absorbed the exception into a result must not quietly turn it into a failed item.
            cancellation.ThrowIfCallerCancelled();

            if (stoppedByBatch)
            {
                var (cancelledCode, cancelledMessage) = cancellation.Classify(item, window);
                return new FanOutItemResult(
                    item.Index, item.ItemKey, false, null, cancelledCode, cancelledMessage, duration);
            }

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
        var data = execution.Response?.Data;

        // The engine completed, so this is the item's OWN verdict and it keeps its own code — unless
        // the failure it reported is itself a cancellation, which only the structured ExceptionType
        // can say (an executor that catches the cancellation and reports it as a task failure rather
        // than letting it escape). Deliberately narrower than the branch above: here a real 5xx from
        // an item that was unlucky enough to fail in the instant a sibling triggered the early stop
        // must NOT be relabelled as cancelled.
        if (stoppedByBatch && IsCancellationException(execution.TaskError?.NormalizedError.ExceptionType))
        {
            var (cancelledCode, cancelledMessage) = cancellation.Classify(item, window);
            return new FanOutItemResult(
                item.Index, item.ItemKey, false, data, cancelledCode, cancelledMessage, duration);
        }

        return new FanOutItemResult(
            item.Index, item.ItemKey, false, data,
            execution.TaskError?.NormalizedError.Code ?? FanOutErrorCodes.ItemFailed,
            execution.TaskError?.ErrorMessage
            ?? execution.Response?.ErrorMessage
            ?? $"FanOut item {item.ItemKey} failed.",
            duration);
    }

    /// <summary>
    /// Whether a normalized error's exception type names a cancellation.
    /// </summary>
    /// <remarks>
    /// <c>NormalizedError.ExceptionType</c> carries the type's NAME, not the type, so a name
    /// comparison is the only comparison available — but it is a comparison against a dedicated
    /// structured field, not a substring hunt through a composed error code. The same two names are
    /// what <c>ErrorNormalizer</c> already keys its transient classification on.
    /// </remarks>
    private static bool IsCancellationException(string? exceptionType) =>
        exceptionType is nameof(OperationCanceledException) or nameof(TaskCanceledException);

    /// <summary>
    /// Produces the batch's single output: the mapping's <c>OutputHandler</c> when one overrode it,
    /// otherwise the default packaging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "No mapping at all" and "a mapping that did not override <c>OutputHandler</c>" are ONE case
    /// here, deciding on one <c>null</c> and falling through to one <see cref="BuildDefaultOutput"/>
    /// call. Branching to the default early for the mapping-less path would leave two routes to the
    /// same documented shape that can drift apart — and overriding input binding must not cost an
    /// author the default output, which is exactly what forced them to reimplement it byte-for-byte
    /// in a script.
    /// </para>
    /// <para>
    /// The signal is a null <see cref="ScriptResponse"/>, not a null <c>Data</c>: a handler that ran
    /// and deliberately produced nothing still replaces the default with nothing.
    /// </para>
    /// </remarks>
    private static async Task<Result<object?>> BuildOutputAsync(
        FanOutTask task,
        IFanOutMapping? mapping,
        TaskExecutorContext context,
        FanOutResult result,
        CancellationToken cancellationToken)
    {
        var handled = mapping is null
            ? Result<ScriptResponse?>.Ok(null)
            : await ResultExtensions.TryAsync<ScriptResponse?>(
                async _ => await mapping.OutputHandler(context.ScriptContext, result),
                cancellationToken,
                ex => Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"FanOut task output handler failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!handled.IsSuccess)
        {
            return Result<object?>.Fail(handled.Error);
        }

        return Result<object?>.Ok(handled.Value is { } response
            ? response.Data
            : BuildDefaultOutput(task, result));
    }

    /// <summary>
    /// The default output shape, used both when the task ships no mapping and when its mapping
    /// leaves <c>OutputHandler</c> unoverridden: the per-item results under the configured result
    /// key, and the batch counters under <c>{resultKey}Summary</c>.
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
