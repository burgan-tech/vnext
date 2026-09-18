using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Execution.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Dapr state store task invoker. Supports the get, set and delete commands. Store-name
/// resolution, the shared <c>custom:</c> key prefix, command routing and result shaping are the
/// shared <see cref="StateStoreInvocation"/> core (composed on <see cref="IStateStoreClient"/>),
/// so this invoker only classifies the returned outcome for this host's logging and metrics.
/// </summary>
public sealed class StateStoreTaskInvoker : ITaskInvoker<StateStoreBinding>
{
    private readonly IStateStoreClient _stateStore;
    private readonly ITaskMetrics _metrics;
    private readonly ILogger<StateStoreTaskInvoker> _logger;

    public StateStoreTaskInvoker(
        IStateStoreClient stateStore,
        ILogger<StateStoreTaskInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _stateStore = stateStore;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.StateStore;

    /// <inheritdoc />
    public Type BindingType => typeof(StateStoreBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<StateStoreBinding> descriptor,
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
        var typedBinding = binding.Deserialize<StateStoreBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize StateStoreBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        StateStoreBinding binding,
        CancellationToken cancellationToken)
    {
        var command = binding.Command ?? string.Empty;

        var result = await StateStoreInvocation.ExecuteAsync(
            _stateStore, binding, TaskType, cancellationToken, taskKey);

        var storeName = StoreNameOf(result);

        if (string.IsNullOrWhiteSpace(storeName))
        {
            // Store-name resolution failed before any state-store operation was attempted —
            // nothing to meter, matching the pre-extraction behavior.
            return result;
        }

        // Cancellation is ordinary traffic (a caller timing out, an instance cancelled mid-call)
        // and must not be counted as a plain "failure" metric or trip anything alerting on this
        // invoker's Error rate — same split every other extracted invoker makes. A state-store
        // result never carries a numeric StatusCode on any path, so WasCancelled's metadata flag
        // (not a status-code check) is the only reliable signal here.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            _metrics.RecordStateStoreOperation(storeName, command, "cancelled");
            _logger.LogWarning("State store operation was cancelled: {StoreName}/{Command}",
                storeName, command);
            return result;
        }

        _metrics.RecordStateStoreOperation(storeName, command, result.IsSuccess ? "success" : "failure");

        // Only an actual thrown exception (the Core's catch-all, stamped with Metadata
        // ["ExceptionType"]) is logged at Error — matching the pre-extraction behavior, where the
        // only two live `catch` blocks were cancellation (Warning, above) and an unhandled
        // exception (Error). A returned validation failure (missing key, bad command, an
        // unresolved store name) was only ever metered, never logged: the Execution host is still
        // the live production path for every domain today, and a chronically misconfigured
        // StateStore task must not newly flood its Error stream where it was previously silent.
        if (!result.IsSuccess && TryGetExceptionType(result, out var exceptionType))
        {
            _logger.LogError(
                "State store operation failed: {StoreName}/{Command}, Error: {Error}, ExceptionType: {ExceptionType}",
                storeName, command, result.ErrorMessage, exceptionType);
        }

        return result;
    }

    private static string? StoreNameOf(TaskInvocationResult result) =>
        result.Metadata?.TryGetValue("StoreName", out var value) == true && value is string name
            ? name
            : null;

    /// <summary>
    /// Reads the exception type name the shared core stamps into <c>Metadata["ExceptionType"]</c>
    /// on its unhandled-exception path. The core swallows the exception itself by contract (it
    /// returns a <see cref="TaskInvocationResult"/>, never throws), so this is both the only way
    /// this wrapper's error log can still name the failure type, and the signal that distinguishes
    /// a genuine thrown exception from a returned validation failure (which sets no such key).
    /// </summary>
    private static bool TryGetExceptionType(TaskInvocationResult result, out string exceptionType)
    {
        if (result.Metadata?.TryGetValue("ExceptionType", out var value) == true && value is string type)
        {
            exceptionType = type;
            return true;
        }

        exceptionType = string.Empty;
        return false;
    }
}
