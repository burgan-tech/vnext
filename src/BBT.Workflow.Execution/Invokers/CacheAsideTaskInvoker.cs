using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Execution.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Cache-Aside (read-through) task invoker. The read-through flow itself (state-store get/set,
/// hit/miss shaping, <c>bypassOnCacheError</c> and <c>forceRefresh</c> semantics, the two cache
/// spans) lives in the shared <see cref="CacheAsideInvocation"/> core so it cannot drift from the
/// Orchestration host's in-process path. This wrapper supplies the two things that legitimately
/// differ by host: how the source task is dispatched on a miss — here, through this service's own
/// <see cref="ITaskInvokerRegistry"/>, exactly as before the extraction — and the diagnostic
/// <c>LogWarning</c> when a cache read/write failure is swallowed under
/// <c>bypassOnCacheError=true</c> (the core reports it through a callback; only the host logs).
/// </summary>
public sealed class CacheAsideTaskInvoker : ITaskInvoker<CacheAsideBinding>
{
    private readonly IStateStoreClient _stateStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CacheAsideTaskInvoker> _logger;

    public CacheAsideTaskInvoker(
        IStateStoreClient stateStore,
        IServiceProvider serviceProvider,
        ILogger<CacheAsideTaskInvoker> logger)
    {
        _stateStore = stateStore;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.CacheAside;

    /// <inheritdoc />
    public Type BindingType => typeof(CacheAsideBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<CacheAsideBinding> descriptor,
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
        var typedBinding = binding.Deserialize<CacheAsideBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize CacheAsideBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        CacheAsideBinding binding,
        CancellationToken cancellationToken)
    {
        // Resolved lazily inside the delegate (not captured up front) so the registry is only ever
        // touched on a cache miss/forceRefresh, matching the pre-extraction call site exactly.
        return CacheAsideInvocation.ExecuteAsync(
            _stateStore,
            binding,
            (sourceEnvelope, ct) =>
                _serviceProvider.GetRequiredService<ITaskInvokerRegistry>().InvokeAsync(sourceEnvelope, ct),
            TaskType,
            cancellationToken,
            taskKey,
            onBypassedCacheError: (stage, ex) => LogBypassedCacheError(stage, ex, taskKey));
    }

    /// <summary>
    /// Reproduces the pre-extraction invoker's two <c>LogWarning</c> lines verbatim (same text, same
    /// exception object, same single <c>taskKey</c> structured field) — restored via
    /// <see cref="CacheAsideInvocation"/>'s notification seam, since the core itself does not log.
    /// </summary>
    private void LogBypassedCacheError(
        CacheAsideInvocation.CacheAsideBypassStage stage, Exception ex, string? taskKey)
    {
        switch (stage)
        {
            case CacheAsideInvocation.CacheAsideBypassStage.Read:
                _logger.LogWarning(ex,
                    "CacheAside {TaskKey}: cache read failed. Falling back to the source task (bypassOnCacheError=true).",
                    taskKey);
                break;
            case CacheAsideInvocation.CacheAsideBypassStage.Write:
                _logger.LogWarning(ex,
                    "CacheAside {TaskKey}: cache write failed. Returning the source result anyway (bypassOnCacheError=true).",
                    taskKey);
                break;
        }
    }
}
