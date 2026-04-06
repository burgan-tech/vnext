using System.Diagnostics.CodeAnalysis;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Qualifier type for a dynamic role grant: determines what the resolved value is compared against.
/// </summary>
public enum DynamicRoleQualifier
{
    /// <summary>Resolved value is compared to <c>ICurrentUser.ActorUserName</c> (act_sub).</summary>
    User,

    /// <summary>Resolved value is compared to <c>ICurrentUser.UserName</c> (sub / behalf-of subject).</summary>
    UserBehalfOf,

    /// <summary>Resolved value is compared to the caller's static role string (OrdinalIgnoreCase).</summary>
    Role
}

/// <summary>
/// Represents a dynamic role grant where the compared value is resolved at evaluation time
/// from the authorization context via a ScriptContext-compatible path.
/// <para>
/// Format: <c>$&lt;qualifier&gt;.$.context.&lt;path&gt;</c>
/// </para>
/// <para>
/// Examples:
/// <list type="bullet">
///   <item><c>$user.$.context.Instance.Data.customer.ownerUserId</c></item>
///   <item><c>$user.$.context.Instance.Data.assignedUsers[*].userId</c></item>
///   <item><c>$userBehalfOf.$.context.Instance.Data.customer.behalfOfUserId</c></item>
///   <item><c>$role.$.context.Instance.Data.permissions.requiredRole</c></item>
///   <item><c>$role.$.context.Transition.Key</c></item>
/// </list>
/// </para>
/// </summary>
public sealed record DynamicRoleGrant(DynamicRoleQualifier Qualifier, string ContextPath)
{
    private const string ContextPrefix = "$.context.";
    private const string UserBehalfOfPrefix = "$userBehalfOf.";
    private const string UserPrefix = "$user.";
    private const string RolePrefix = "$role.";

    /// <summary>Returns true if the context path contains an array wildcard segment (<c>[*]</c>).</summary>
    public bool IsArrayPath => ContextPath.Contains("[*]", StringComparison.Ordinal);

    /// <summary>
    /// Attempts to parse a role grant string as a dynamic role grant.
    /// Returns null if the string is not a dynamic role grant pattern.
    /// </summary>
    /// <remarks>
    /// Checks <c>$userBehalfOf</c> before <c>$user</c> to avoid prefix collision.
    /// </remarks>
    public static DynamicRoleGrant? TryParse(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return null;

        DynamicRoleQualifier qualifier;
        string remainder;

        // Check $userBehalfOf BEFORE $user to avoid prefix collision
        if (role.StartsWith(UserBehalfOfPrefix, StringComparison.Ordinal))
        {
            qualifier = DynamicRoleQualifier.UserBehalfOf;
            remainder = role[UserBehalfOfPrefix.Length..];
        }
        else if (role.StartsWith(UserPrefix, StringComparison.Ordinal))
        {
            qualifier = DynamicRoleQualifier.User;
            remainder = role[UserPrefix.Length..];
        }
        else if (role.StartsWith(RolePrefix, StringComparison.Ordinal))
        {
            qualifier = DynamicRoleQualifier.Role;
            remainder = role[RolePrefix.Length..];
        }
        else
        {
            return null;
        }

        if (!remainder.StartsWith(ContextPrefix, StringComparison.Ordinal))
            return null;

        // Validate that there is a non-empty navigation path after the "$.context." prefix
        var navigationPath = remainder[ContextPrefix.Length..];
        if (string.IsNullOrWhiteSpace(navigationPath))
            return null;

        // Store the full path including "$.context." — ResolveDynamicRoleMatch will strip the prefix during evaluation
        return new DynamicRoleGrant(qualifier, remainder);
    }

    /// <summary>Returns true if the given role string matches the dynamic role grant pattern.</summary>
    public static bool IsDynamicRole([NotNullWhen(true)] string? role) => TryParse(role) != null;
}
