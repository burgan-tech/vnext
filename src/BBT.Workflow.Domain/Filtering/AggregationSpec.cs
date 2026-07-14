namespace BBT.Workflow.Filtering;

/// <summary>
/// Aggregation functions to apply to a list query (optionally per group). Field names use the same
/// wire convention as the list API — instance columns bare (e.g. <c>createdAt</c>) and attributes
/// dotted with the prefix (e.g. <c>attributes.amount</c>).
/// </summary>
public sealed class AggregationSpec
{
    public AggregationSpec(bool count, string? sum, string? avg, string? min, string? max)
    {
        Count = count;
        Sum = sum;
        Avg = avg;
        Min = min;
        Max = max;
    }

    /// <summary>Emit <c>count</c> (COUNT(*)).</summary>
    public bool Count { get; }

    /// <summary>Field to sum (or null).</summary>
    public string? Sum { get; }

    /// <summary>Field to average (or null).</summary>
    public string? Avg { get; }

    /// <summary>Field for minimum (or null).</summary>
    public string? Min { get; }

    /// <summary>Field for maximum (or null).</summary>
    public string? Max { get; }

    /// <summary>True when at least one aggregation is requested.</summary>
    public bool HasAny => Count || Sum is not null || Avg is not null || Min is not null || Max is not null;
}
