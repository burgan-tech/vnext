using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Rules;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// The <c>instance</c> variable of an <see cref="ExpressionScope"/>: a read-only view of the running instance.
/// Member names are camelCase, the way the JSON variables (<c>data</c>, <c>input</c>, <c>result</c>) and
/// <see cref="ExpressionUser"/> spell theirs, so an expression reads <c>instance.currentState</c>. The legacy
/// PascalCase view (<see cref="ExpressoInstanceView"/>) stays reachable through <c>context.Instance</c>.
/// Every member reads the instance on access; <c>data</c> borrows the latest data once (never cloned).
/// </summary>
#pragma warning disable IDE1006 // camelCase members are the expression-language surface
public sealed class ExpressionInstance
{
    private readonly Instance _instance;
    private RuleJsonDynamic? _data;

    internal ExpressionInstance(Instance instance) => _instance = instance;

    /// <summary>Instance identifier.</summary>
    public Guid id => _instance.Id;

    /// <summary>Business key, when the instance has one.</summary>
    public string? key => _instance.Key;

    /// <summary>Flow key.</summary>
    public string flow => _instance.Flow;

    /// <summary>Flow version the instance was started with.</summary>
    public string? version => _instance.FlowVersion;

    /// <summary>Engine current state key.</summary>
    public string? currentState => _instance.CurrentState;

    /// <summary>Externally visible state key (the deepest active SubFlow's state while one runs).</summary>
    public string? effectiveState => _instance.EffectiveState;

    /// <summary>Effective state type name (for example <c>"Intermediate"</c>).</summary>
    public string? effectiveStateType => _instance.EffectiveStateType?.ToString();

    /// <summary>Effective state subtype name (for example <c>"Human"</c>).</summary>
    public string? effectiveStateSubType => _instance.EffectiveStateSubType?.ToString();

    /// <summary>Status code a client observes (<c>"A"</c>, <c>"B"</c>, <c>"C"</c>, <c>"F"</c>, <c>"P"</c>).</summary>
    public string effectiveStatus => _instance.GetEffectiveStatus.Code;

    /// <summary>How the instance was started: <c>"R"</c> root, <c>"S"</c> SubFlow, <c>"P"</c> SubProcess.</summary>
    public string type => _instance.Type.Code;

    /// <summary>The latest data, the same value as the <c>data</c> variable.</summary>
    public RuleJsonDynamic data => _data ??= _instance.LatestData is { } latest
        ? RuleJsonDynamic.Borrow(latest.Data.JsonElement)
        : RuleJsonDynamic.Empty;
}
#pragma warning restore IDE1006
