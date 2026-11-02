namespace BBT.Workflow.SubFlow.Headers;

/// <summary>
/// Filters the request headers carried from a SubFlow child's settlement to its parent.
/// </summary>
public interface ICarriedHeaderPolicy
{
    /// <summary>
    /// Producer side. Filters the headers of the settling request. Returns <c>null</c> when
    /// <c>Emit</c> is off or nothing survives the filter.
    /// </summary>
    Dictionary<string, string?>? Capture(IReadOnlyDictionary<string, string?>? headers);

    /// <summary>
    /// Receiver side. Re-filters the carried headers; never returns <c>null</c>. The result is
    /// case-insensitive.
    /// </summary>
    Dictionary<string, string?> Apply(IReadOnlyDictionary<string, string?>? carried);

    /// <summary>Mirrors <see cref="SubFlowCarriedHeadersOptions.ExposeToOutputMapping"/>.</summary>
    bool ExposeToOutputMapping { get; }

    /// <summary>Mirrors <see cref="SubFlowCarriedHeadersOptions.ApplyToResume"/>.</summary>
    bool ApplyToResume { get; }
}
