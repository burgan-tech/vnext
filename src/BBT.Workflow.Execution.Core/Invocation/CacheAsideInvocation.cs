using System.Diagnostics;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.StateStores;

namespace BBT.Workflow.Execution.Core.Invocation;

/// <summary>
/// The single implementation of the cache-aside (read-through) task call, shared by both hosts:
/// the Execution service's <c>CacheAsideTaskInvoker</c> and the Orchestrator's in-process path
/// delegate here, so the two cannot drift behaviorally — the read-through flow (read, hit
/// short-circuit, dispatch the source task on a miss/forceRefresh, best-effort write-back), the
/// <c>bypassOnCacheError</c> / <c>forceRefresh</c> semantics and the result/metadata shaping are
/// defined exactly once, composed on top of the shared <see cref="IStateStoreClient"/> (the same
/// gateway the State Store task uses, so a cache-aside entry and a state-store entry for the same
/// logical key always hit the same physical record).
/// <para>
/// Unlike the other extracted cores, the source task on a miss is not a fixed call this core can
/// make itself: each host reaches its source task through a DIFFERENT dispatch surface (the
/// Execution service's own <c>ITaskInvokerRegistry</c>, versus the Orchestration host's local
/// invoker registry with a remote fallback for source types that have no in-process invoker). That
/// call is therefore taken as a delegate (<c>dispatchSource</c>, below) rather than resolved here.
/// </para>
/// <para>
/// Lives in <c>Execution.Core</c> for the same reason as <see cref="StateStoreInvocation"/>: the
/// Orchestration host cannot reference <c>BBT.Workflow.Execution</c> (pythonnet, KubernetesClient,
/// Dapr.AI, a hosted service). Mirrors <see cref="StateStoreInvocation"/> /
/// <see cref="SoapInvocation"/> / <see cref="DaprServiceInvocation"/>: no logging or metrics happen
/// here. One deliberate consequence of that: the pre-extraction invoker logged a warning at Warning
/// level when a cache read/write error was swallowed under <c>bypassOnCacheError</c> — this core
/// cannot reproduce that log line (no logger dependency), and the two hosts that call it cannot
/// reconstruct it reliably either, since a subsequent source-task failure/null-data must be
/// returned UNCHANGED (see below) and the success metadata shape is fixed
/// (<see cref="BuildMetadata"/>). Only the two genuine failure paths (a read/write error WITHOUT
/// bypass) still carry an <c>ExceptionType</c> metadata entry for a host to log at Error, matching
/// every other extracted core.
/// </para>
/// </summary>
public static class CacheAsideInvocation
{
    /// <summary>
    /// Deliberately the SAME source name as the Execution host's <c>InvokerActivityHelper</c> and
    /// as the other extracted invocation cores (ActivitySource listeners match by name): the
    /// <c>CacheAside.Read</c> / <c>CacheAside.Write</c> spans this core starts are the same span
    /// family the host's own <c>InvokerActivityHelper.StartCacheAsideActivity</c> produces, and the
    /// Execution host's <c>BBT.Workflow.Execution*</c> AdditionalSources wildcard exports it
    /// without new config. The helper itself lives in the host assembly, which this project cannot
    /// reference.
    /// </summary>
    private static readonly ActivitySource InvokerActivitySource = new("BBT.Workflow.Execution.Invokers");

    /// <summary>
    /// Starts a span for one step of the cache-aside protocol, so a hit and a miss are told apart
    /// in the trace tree rather than inferred from whether a source-task span happens to follow.
    /// Mirrors <c>InvokerActivityHelper.StartCacheAsideActivity</c> exactly (same span name shape
    /// and <see cref="ActivityKind"/>).
    /// </summary>
    private static Activity? StartCacheAsideActivity(string operation, string cacheKey) =>
        InvokerActivitySource.StartActivity($"CacheAside.{operation}/{cacheKey}", ActivityKind.Client);

    /// <summary>Records whether the cache-aside read was a hit. Mirrors <c>InvokerActivityHelper.SetCacheHit</c>.</summary>
    private static void SetCacheHit(Activity? activity, bool hit) => activity?.SetTag("cache.hit", hit);

    /// <summary>
    /// Executes the cache-aside read-through described by the binding. Never throws — besides a
    /// caller-requested cancellation during a state-store call, which is rethrown as-is, matching
    /// the pre-extraction behavior exactly (cache-aside never returns a "cancelled" result of its
    /// own for that case). Store-name resolution failure, a cache read/write error without bypass,
    /// and the source task's own outcome all become <see cref="TaskInvocationResult"/>s so the
    /// caller's error boundary decides.
    /// </summary>
    /// <param name="stateStore">The shared state-store gateway (store-name resolution, the
    /// <c>custom:</c> prefix, TTL/consistency handling) — the same one the State Store task uses.</param>
    /// <param name="binding">The prepared cache-aside binding (key, options, source envelope).</param>
    /// <param name="dispatchSource">Runs the pre-resolved source task on a miss/forceRefresh. Each
    /// host supplies its own dispatch surface (Execution: its own task-invoker registry;
    /// Orchestration: the local invoker registry, falling back to the Execution service for a
    /// source type with no in-process invoker).</param>
    /// <param name="taskType">Task-type label stamped on the result (each host stamps its own).</param>
    /// <param name="cancellationToken">Caller cancellation; a fire during a cache read/write is
    /// rethrown rather than converted into a failed result.</param>
    /// <param name="taskKey">Task key. Currently unused by this core (the pre-extraction invoker
    /// only used it in the LogWarning lines this core does not reproduce — see the type doc) but
    /// kept in the signature for parity with the other extracted cores.</param>
    public static async Task<TaskInvocationResult> ExecuteAsync(
        IStateStoreClient stateStore,
        CacheAsideBinding binding,
        Func<TaskEnvelope, CancellationToken, Task<TaskInvocationResult>> dispatchSource,
        string taskType,
        CancellationToken cancellationToken,
        string? taskKey = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return TaskInvocationResult.Failure(
                error: "CacheAside requires a non-empty 'key'.",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType);
        }

        var storeName = stateStore.ResolveStoreName(binding.StoreName);
        var canCache = !string.IsNullOrWhiteSpace(storeName);

        if (!canCache && !binding.BypassOnCacheError)
        {
            return TaskInvocationResult.Failure(
                error: "State store name is not configured: set 'storeName' in the task config " +
                       "or the DAPR_STATE_STORE_NAME configuration value.",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType);
        }

        // 1. Cache read (unless forceRefresh / uncacheable).
        if (canCache && !binding.ForceRefresh)
        {
            try
            {
                // The hit/miss decision is the whole point of this task type; without a span for it
                // a reader can only infer the outcome from whether a source-task span follows.
                using var readActivity = StartCacheAsideActivity("Read", binding.Key);
                var entry = await stateStore.GetAsync(
                    storeName!, binding.Key, binding.Consistency, metadata: null, cancellationToken);
                SetCacheHit(readActivity, entry.Found);

                if (entry.Found)
                {
                    return TaskInvocationResult.Success(
                        data: entry.Value,
                        body: entry.Value.GetRawText(),
                        executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                        taskType: taskType,
                        metadata: BuildMetadata(stateStore, binding, storeName!, cacheHit: true, refreshed: false, entry.ETag));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!binding.BypassOnCacheError)
                {
                    return TaskInvocationResult.Failure(
                        error: $"CacheAside read failed: {ex.Message}",
                        executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                        taskType: taskType,
                        metadata: new Dictionary<string, object> { ["ExceptionType"] = ex.GetType().Name });
                }

                // bypassOnCacheError=true: swallow and fall through to the source task, exactly as
                // the pre-extraction invoker did. See the type doc — the pre-extraction LogWarning
                // at this exact point cannot be reproduced here or reconstructed by a caller.
            }
        }

        // 2. Cache miss / forceRefresh: dispatch the source task through the delegate.
        var sourceResult = await dispatchSource(binding.SourceTask, cancellationToken);
        if (!sourceResult.IsSuccess || sourceResult.Data is null)
        {
            // Propagate the source outcome unchanged (a business failure flows to the error boundary chain;
            // a null result is simply not cached).
            return sourceResult;
        }

        // 3. Best-effort cache write.
        if (canCache)
        {
            try
            {
                using var writeActivity = StartCacheAsideActivity("Write", binding.Key);
                await stateStore.SetAsync(
                    storeName!, binding.Key, sourceResult.Data, binding.TtlInSeconds, binding.Consistency,
                    concurrency: null, etag: null, metadata: null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!binding.BypassOnCacheError)
                {
                    return TaskInvocationResult.Failure(
                        error: $"CacheAside write failed: {ex.Message}",
                        executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                        taskType: taskType,
                        metadata: new Dictionary<string, object> { ["ExceptionType"] = ex.GetType().Name });
                }

                // bypassOnCacheError=true: swallow and return the source result anyway. Same
                // LogWarning-reproduction note as the read path above.
            }
        }

        return TaskInvocationResult.Success(
            data: sourceResult.Data,
            body: sourceResult.Body,
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: taskType,
            metadata: BuildMetadata(stateStore, binding, storeName ?? string.Empty, cacheHit: false, refreshed: true, etag: null));
    }

    private static Dictionary<string, object> BuildMetadata(
        IStateStoreClient stateStore,
        CacheAsideBinding binding,
        string storeName,
        bool cacheHit,
        bool refreshed,
        string? etag) => new()
    {
        ["StoreName"] = storeName,
        ["Key"] = stateStore.PrefixKey(binding.Key),
        ["CacheHit"] = cacheHit,
        ["Refreshed"] = refreshed,
        ["ETag"] = etag ?? string.Empty
    };
}
