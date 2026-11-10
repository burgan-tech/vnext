using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Row-level security for instance list and read surfaces of a workflow (<c>attributes.rowSecurity</c>,
/// vnext#1107). JSON: <c>{ "rules": [ { "roles": [...], "filter": { ... } } ] }</c>.
/// </summary>
/// <remarks>
/// There is no <c>default</c>: a caller that matches no rule sees no rows (deny by default). A member
/// the runtime does not know — a <c>default</c> left over from the pre-K1 draft included — is kept in
/// <see cref="UnknownMembers"/> so that publish validation can reject it (RS-E12) instead of silently
/// ignoring it.
/// </remarks>
public sealed class RowSecurityDefinition
{
    /// <summary>The rules; a caller's visible rows are the union of the rules whose roles it holds.</summary>
    [JsonPropertyName("rules")]
    public List<RowSecurityRule>? Rules { get; init; }

    /// <summary>Members not recognized by the runtime; null when there are none.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownMembers { get; init; }
}

/// <summary>
/// One row-security rule: the callers it applies to (<see cref="Roles"/>) and the rows they may see
/// (<see cref="Filter"/>, raw filter JSON parsed by <see cref="RowFilterTemplateParser"/>).
/// </summary>
public sealed class RowSecurityRule
{
    /// <summary>
    /// Static role names, or exactly one of the reserved roles <c>$InstanceStarter</c> /
    /// <c>$InstanceBehalfOfStarter</c>. See <see cref="Audience"/>.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<string>? Roles { get; init; }

    /// <summary>
    /// Raw filter JSON in the instance-filter shape, with <c>$.context.*</c> parameters. Absent means
    /// the rule grants every row (a static rule) or the audience's own rows (a reserved-role rule).
    /// </summary>
    [JsonPropertyName("filter")]
    public JsonElement? Filter { get; init; }

    /// <summary>Members not recognized by the runtime; null when there are none.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownMembers { get; init; }

    /// <summary>Who the rule applies to, classified from <see cref="Roles"/>.</summary>
    [JsonIgnore]
    public RowRuleAudience Audience => ClassifyAudience(Roles);

    private static RowRuleAudience ClassifyAudience(List<string>? roles)
    {
        if (roles is null || roles.Count == 0)
            return RowRuleAudience.Invalid;

        var hasStatic = false;
        var hasStarter = false;
        var hasBehalfOfStarter = false;

        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role))
                return RowRuleAudience.Invalid;

            if (string.Equals(role, PredefinedInstanceRoles.InstanceStarter, StringComparison.Ordinal))
                hasStarter = true;
            else if (string.Equals(role, PredefinedInstanceRoles.InstanceBehalfOfStarter, StringComparison.Ordinal))
                hasBehalfOfStarter = true;
            else if (role.StartsWith('$'))
                return RowRuleAudience.Invalid; // other reserved roles and dynamic grants
            else
                hasStatic = true;
        }

        var kinds = (hasStatic ? 1 : 0) + (hasStarter ? 1 : 0) + (hasBehalfOfStarter ? 1 : 0);
        if (kinds > 1)
            return RowRuleAudience.Mixed;

        return hasStarter ? RowRuleAudience.InstanceStarter
            : hasBehalfOfStarter ? RowRuleAudience.InstanceBehalfOfStarter
            : RowRuleAudience.Static;
    }
}

/// <summary>
/// Audience of a <see cref="RowSecurityRule"/>, derived from its roles.
/// </summary>
public enum RowRuleAudience
{
    /// <summary>Static role names only.</summary>
    Static,

    /// <summary>Only <c>$InstanceStarter</c>: the actor who started the instance.</summary>
    InstanceStarter,

    /// <summary>Only <c>$InstanceBehalfOfStarter</c>: the subject the instance was started on behalf of.</summary>
    InstanceBehalfOfStarter,

    /// <summary>More than one of the above kinds in one rule.</summary>
    Mixed,

    /// <summary>No roles, a blank role, another reserved role or a dynamic grant.</summary>
    Invalid
}
