namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Resolves in-process task invokers by wire task type from the DI-registered set. Mirrors
/// <c>TaskExecutorRegistry</c>: no caching beyond the lookup table it builds once per scope,
/// so it stays safe for scoped invokers.
/// </summary>
public sealed class LocalTaskInvokerRegistry : ILocalTaskInvokerRegistry
{
    private readonly Dictionary<string, ILocalTaskInvoker> _invokers;

    public LocalTaskInvokerRegistry(IEnumerable<ILocalTaskInvoker> invokers)
    {
        _invokers = new Dictionary<string, ILocalTaskInvoker>(StringComparer.OrdinalIgnoreCase);
        foreach (var invoker in invokers)
        {
            // Last registration wins, matching the DI convention where a later AddScoped
            // overrides an earlier one for the same key.
            _invokers[invoker.TaskType] = invoker;
        }
    }

    /// <inheritdoc />
    public ILocalTaskInvoker? Get(string taskType) =>
        _invokers.GetValueOrDefault(taskType);

    /// <inheritdoc />
    public bool Has(string taskType) => _invokers.ContainsKey(taskType);
}
