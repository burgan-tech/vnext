using System.Text.Json;
using BBT.Aether;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// The one parser of the two SubFlow override stamps <c>SubflowStarter</c> writes onto a child:
/// <see cref="DomainConsts.MetaDataKeys.StateRoleOverrides"/> and
/// <see cref="DomainConsts.MetaDataKeys.TransitionRoleOverrides"/>. Despite the key names, both carry
/// the parent's WHOLE per-state / per-transition override maps (query roles, transition roles,
/// long-poll, views).
/// </summary>
/// <remarks>
/// Written with default serializer options, read with the shared ones: RoleGrant's properties are
/// PascalCase and its constructor parameters camelCase, so only a case-insensitive read succeeds.
/// A malformed stamp degrades to "no override" — falling back to the child's own configuration,
/// never to allowing everything — and reports <c>malformed</c> so a caller can log it.
/// </remarks>
public static class SubFlowOverrideStamp
{
    /// <summary>
    /// Parses the parent-stamped per-state override map from the child's metadata, or null when the
    /// instance carries no stamp for this key or it cannot be read.
    /// </summary>
    public static Dictionary<string, SubFlowStateOverride>? ReadStates(
        ExtraPropertyDictionary? metaData, out bool malformed)
        => Read<SubFlowStateOverride>(metaData, DomainConsts.MetaDataKeys.StateRoleOverrides, out malformed);

    /// <summary>
    /// Parses the parent-stamped per-transition override map from the child's metadata, or null when
    /// the instance carries no stamp for this key or it cannot be read.
    /// </summary>
    public static Dictionary<string, SubFlowTransitionOverride>? ReadTransitions(
        ExtraPropertyDictionary? metaData, out bool malformed)
        => Read<SubFlowTransitionOverride>(metaData, DomainConsts.MetaDataKeys.TransitionRoleOverrides, out malformed);

    private static Dictionary<string, T>? Read<T>(ExtraPropertyDictionary? metaData, string key, out bool malformed)
    {
        malformed = false;

        // Gate before parsing: an instance with no stamp pays nothing.
        if (metaData is null || !metaData.TryGetValue(key, out var raw) || raw is null)
            return null;

        var json = raw.ToString();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, T>>(json, JsonSerializerConstants.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // ArgumentException: RoleGrant's constructor rejects an empty role while deserializing.
            malformed = true;
            return null;
        }
    }
}
