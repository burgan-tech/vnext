using System.Collections;
using System.Text;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// The per-hop logging scope <see cref="TransitionExecutor"/> opens around a transition's steps.
/// <para>
/// Replaces a <c>Dictionary&lt;string, object&gt;</c> built on every hop: same keys, same values, same
/// order (the dictionary's insertion order), captured when the scope is created — so a value that changes
/// during the pipeline (the instance's current state, say) is logged as it was at scope creation, exactly
/// as before. Logging providers (OpenTelemetry, Serilog, the console formatters) enumerate a scope as
/// <see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>, which this list is; the
/// scope keys OpenObserve queries filter on are unchanged.
/// </para>
/// </summary>
internal sealed class TransitionLogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private const int FixedCount = 12;

    private readonly KeyValuePair<string, object?>[] _items;

    public TransitionLogScope(
        string domain,
        string flow,
        string flowVersion,
        Guid instanceId,
        string instanceKey,
        string stateFrom,
        string stateTo,
        string transitionKey,
        string triggerType,
        string correlationId,
        int chainDepth,
        string pipelineProfile,
        Guid? parentInstanceId,
        Guid? rootInstanceId)
    {
        var count = FixedCount + (parentInstanceId.HasValue ? 1 : 0) + (rootInstanceId.HasValue ? 1 : 0);
        var items = new KeyValuePair<string, object?>[count];
        items[0] = new(TelemetryConstants.TagNames.Domain, domain);
        items[1] = new(TelemetryConstants.TagNames.Flow, flow);
        items[2] = new(TelemetryConstants.TagNames.FlowVersion, flowVersion);
        items[3] = new(TelemetryConstants.TagNames.InstanceId, instanceId);
        items[4] = new(TelemetryConstants.TagNames.InstanceKey, instanceKey);
        items[5] = new(TelemetryConstants.TagNames.StateFrom, stateFrom);
        items[6] = new(TelemetryConstants.TagNames.StateTo, stateTo);
        items[7] = new(TelemetryConstants.TagNames.TransitionKey, transitionKey);
        items[8] = new(TelemetryConstants.TagNames.TriggerType, triggerType);
        items[9] = new(TelemetryConstants.TagNames.CorrelationId, correlationId);
        items[10] = new("ChainDepth", chainDepth);
        items[11] = new("PipelineProfile", pipelineProfile);

        var next = FixedCount;
        if (parentInstanceId is { } parentId)
            items[next++] = new(TelemetryConstants.TagNames.ParentInstanceId, parentId);
        if (rootInstanceId is { } rootId)
            items[next] = new(TelemetryConstants.TagNames.RootInstanceId, rootId);

        _items = items;
    }

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>"key:value, …" — what a text formatter prints for the scope.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _items.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(_items[i].Key).Append(':').Append(_items[i].Value);
        }

        return sb.ToString();
    }
}
