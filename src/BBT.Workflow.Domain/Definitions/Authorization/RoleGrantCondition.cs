using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// One leaf of a <see cref="RoleGrant"/> combinator (<c>allOf</c> / <c>anyOf</c>).
/// JSON format: { "role": "morph-idm.maker" }. Combinators do not nest, so a leaf is only a role.
/// </summary>
// Depth 1 is enforced at the wire: any extra member (a 'grant', a nested combinator) fails deserialization.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RoleGrantCondition
{
    private RoleGrantCondition()
    {
    }

    [JsonConstructor]
    internal RoleGrantCondition(string role)
        => Role = Check.NotNullOrWhiteSpace(role, nameof(Role), RoleGrant.MaxRoleLength);

    /// <summary>
    /// Role identifier or dynamic role (e.g. morph-idm.maker, $InstanceStarter).
    /// </summary>
    public string Role { get; private set; } = string.Empty;
}
