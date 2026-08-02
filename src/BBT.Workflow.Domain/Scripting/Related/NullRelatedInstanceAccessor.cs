namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// No-op accessor used when a ScriptContext is built without a reader — unit tests and any code path
/// that constructs <c>ScriptContext.Builder</c> directly. Reports no parent and no correlations so
/// scripts and existing tests behave as if the instance were standalone.
/// </summary>
public sealed class NullRelatedInstanceAccessor : IRelatedInstanceAccessor
{
    /// <summary>The shared stateless instance.</summary>
    public static readonly NullRelatedInstanceAccessor Instance = new();

    private NullRelatedInstanceAccessor()
    {
    }

    /// <inheritdoc />
    public bool HasParent => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<RelatedInstanceView?>(null);

    /// <inheritdoc />
    public Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<RelatedInstanceView?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RelatedInstanceView>>([]);
}
