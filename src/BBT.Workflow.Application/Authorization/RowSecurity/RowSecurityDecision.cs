using BBT.Aether.Results;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>What row security decided for one request (spec §4).</summary>
public enum RowSecurityOutcome
{
    /// <summary>The flow declares no <c>rowSecurity</c>: no row filtering, no tag.</summary>
    NotConfigured,

    /// <summary>A matched static rule has no filter: every row.</summary>
    Unrestricted,

    /// <summary>The caller sees the rows of <see cref="RowSecurityDecision.Predicate"/>.</summary>
    Filtered,

    /// <summary>No static rule matched and no identity rule applied: no rows.</summary>
    NoRule,

    /// <summary>Some rule applied but every one of them was dropped: no rows.</summary>
    NoContribution,

    /// <summary>The caller's role resolution failed: no rows, nothing else evaluated.</summary>
    DeniedResolutionFailed,

    /// <summary>The flow definition (rule set) could not be loaded: no rows.</summary>
    DeniedRuleSetUnavailable
}

/// <summary>Observability vocabulary of <see cref="RowSecurityOutcome"/> (controller ruling R-F11).</summary>
public static class RowSecurityOutcomeExtensions
{
    /// <summary>
    /// The tag value for <paramref name="outcome"/>; null for <see cref="RowSecurityOutcome.NotConfigured"/>,
    /// which carries no row-security tag at all. <c>excluded</c> is not an outcome: the app service writes it
    /// after a GET probe that found no visible row.
    /// </summary>
    public static string? ToTagValue(this RowSecurityOutcome outcome) => outcome switch
    {
        RowSecurityOutcome.Unrestricted => "unrestricted",
        RowSecurityOutcome.Filtered => "filtered",
        RowSecurityOutcome.NoRule => "denied-no-match",
        RowSecurityOutcome.NoContribution => "denied-no-contribution",
        RowSecurityOutcome.DeniedResolutionFailed => "denied-resolution-failed",
        RowSecurityOutcome.DeniedRuleSetUnavailable => "denied-flow-load",
        _ => null
    };
}

/// <summary>
/// The planner's decision for one request. Carries parameter NAMES only, never their values.
/// </summary>
/// <param name="Outcome">The decision kind.</param>
/// <param name="Predicate">The row predicate when <see cref="RowSecurityOutcome.Filtered"/>; otherwise null.</param>
/// <param name="MatchedRuleIndexes">Indexes of the rules that applied to the caller (static role match, or an identity rule whose identity resolved).</param>
/// <param name="MissingParameterNames">Names of the parameters that did not resolve.</param>
/// <param name="DroppedRules">Applied rules that contributed nothing, with the reason.</param>
/// <param name="FlowVersion">The flow version the rule set was read from, when known.</param>
/// <param name="LookupError">The rule-set lookup error for <see cref="RowSecurityOutcome.DeniedRuleSetUnavailable"/>.</param>
public sealed record RowSecurityDecision(
    RowSecurityOutcome Outcome,
    GraphQLFilterNode? Predicate,
    IReadOnlyList<int> MatchedRuleIndexes,
    IReadOnlyList<string> MissingParameterNames,
    IReadOnlyList<RowRuleDrop> DroppedRules,
    string? FlowVersion,
    Error? LookupError = null)
{
    /// <summary>True when the request must return no rows, decided without running SQL.</summary>
    public bool ReturnsNoRows => Outcome is RowSecurityOutcome.NoRule or RowSecurityOutcome.NoContribution
        or RowSecurityOutcome.DeniedResolutionFailed or RowSecurityOutcome.DeniedRuleSetUnavailable;
}

/// <summary>A rule that applied to the caller but contributed nothing.</summary>
/// <param name="RuleIndex">Index of the rule in <c>rowSecurity.rules</c>.</param>
/// <param name="Reason">One of <see cref="RowRuleDropReasons"/>.</param>
/// <param name="ParameterNames">The parameter names involved (names only, never values).</param>
public sealed record RowRuleDrop(int RuleIndex, string Reason, IReadOnlyList<string> ParameterNames);

/// <summary>Values of <see cref="RowRuleDrop.Reason"/>.</summary>
public static class RowRuleDropReasons
{
    /// <summary>A parameter was absent or blank.</summary>
    public const string Missing = "missing";

    /// <summary>A scalar operator was fed more than one value.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>An operator got the wrong number of values (e.g. <c>between</c>).</summary>
    public const string Arity = "arity";

    /// <summary>A value exceeded <see cref="RowSecurityOptions"/> limits.</summary>
    public const string Cap = "cap";

    /// <summary>A value could not be coerced to the field type.</summary>
    public const string Coercion = "coercion";

    /// <summary>The rule touches a protected (masked / encrypted / role-restricted) path.</summary>
    public const string ProtectedPath = "protected-path";

    /// <summary>The rule's filter could not be parsed (publish validation was bypassed).</summary>
    public const string Invalid = "invalid";
}

/// <summary>Everything the planner reads for one request.</summary>
/// <param name="Definition">The flow's <c>rowSecurity</c>.</param>
/// <param name="Roles">The caller's role set and how it was obtained.</param>
/// <param name="Parameters">
/// Request parameter values; also the ONLY source of the caller's identity
/// (<c>CurrentUser.ActorUserName</c> / <c>CurrentUser.UserName</c>, ruling R-F6(d)).
/// </param>
/// <param name="Schema">The flow's master-schema filter metadata, when resolved.</param>
/// <param name="Exposure">The master schema's masking / role metadata.</param>
/// <param name="Limits">Parameter size limits.</param>
public sealed record RowSecurityPlanInput(
    RowSecurityDefinition Definition,
    CallerRoleResolution Roles,
    IRowSecurityParameterSource Parameters,
    SchemaFilterContext? Schema,
    SchemaExposureMetadata Exposure,
    RowSecurityOptions Limits);
