namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Allowlisted projection of script execution context for Dynamic Expresso condition evaluation.
/// </summary>
public sealed class ExpressoRuleContext
{
    /// <summary>Current merged body / task response payload.</summary>
    public RuleJsonDynamic Body { get; init; } = RuleJsonDynamic.Empty;

    /// <summary>Persisted original transition request, if any.</summary>
    public ExpressoCurrentTransitionView? CurrentTransition { get; init; }

    /// <summary>Execution metadata as a JSON object.</summary>
    public RuleJsonDynamic MetaData { get; init; } = RuleJsonDynamic.Empty;

    /// <summary>Workflow definition subset.</summary>
    public ExpressoWorkflowView? Workflow { get; init; }

    /// <summary>Instance snapshot subset.</summary>
    public ExpressoInstanceView? Instance { get; init; }

    /// <summary>Request headers (JSON object).</summary>
    public RuleJsonDynamic Headers { get; init; } = RuleJsonDynamic.Empty;

    /// <summary>Query string parameters (JSON object).</summary>
    public RuleJsonDynamic QueryParameters { get; init; } = RuleJsonDynamic.Empty;

    /// <summary>Route values from the request (JSON object).</summary>
    public RuleJsonDynamic RouteValues { get; init; } = RuleJsonDynamic.Empty;

    /// <summary>Current transition definition subset (may be null if not set on script context).</summary>
    public ExpressoTransitionView? Transition { get; init; }

    /// <summary>Runtime domain and version (may be null if not set on script context).</summary>
    public ExpressoRuntimeView? Runtime { get; init; }
}
