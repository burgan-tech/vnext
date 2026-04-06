namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Original transition request body and headers exposed to Dynamic Expresso rules.
/// </summary>
public sealed class ExpressoCurrentTransitionView
{
    /// <summary>
    /// Original transition body (JSON object semantics).
    /// </summary>
    public RuleJsonDynamic Data { get; init; } = RuleJsonDynamic.Empty;

    /// <summary>
    /// Original transition headers (JSON object semantics).
    /// </summary>
    public RuleJsonDynamic Header { get; init; } = RuleJsonDynamic.Empty;
}
