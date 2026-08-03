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

    /// <summary>
    /// Classifies a role grant string for definition-time validation.
    /// <para>
    /// <see cref="TryParse"/> cannot serve validation on its own: it collapses "not a dynamic role"
    /// and "a dynamic role the author got wrong" into the same null result, so a typo such as
    /// <c>$user.customer</c> is indistinguishable from a plain static role name — and at runtime it
    /// silently degrades into one (<c>IsMatch</c> falls through to the static comparison, which such a
    /// value can never satisfy). This method recognises the author's intent from the qualifier prefix
    /// first, then reports what is wrong with the remainder.
    /// </para>
    /// <para>
    /// It deliberately reuses the same prefix constants and the same <see cref="StringComparison.Ordinal"/>
    /// comparisons as <see cref="TryParse"/>, so the two can never disagree: for any role,
    /// <c>Classify(role) == <see cref="DynamicRoleFormat.WellFormed"/></c> exactly when
    /// <c>TryParse(role) != null</c>. Validating with a looser comparison would accept values the
    /// runtime then ignores — a case variant like <c>$user.$.Context.x</c> is rejected here precisely
    /// because <see cref="TryParse"/> rejects it too.
    /// </para>
    /// </summary>
    public static DynamicRoleFormat Classify(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return DynamicRoleFormat.NotDynamic;

        string? remainder = null;

        // Check $userBehalfOf BEFORE $user to avoid prefix collision
        foreach (var prefix in new[] { UserBehalfOfPrefix, UserPrefix, RolePrefix })
        {
            if (!role.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            remainder = role[prefix.Length..];
            break;
        }

        // No qualifier prefix: a static role name or one of the four predefined instance roles.
        if (remainder is null)
            return DynamicRoleFormat.NotDynamic;

        if (!remainder.StartsWith(ContextPrefix, StringComparison.Ordinal))
            return DynamicRoleFormat.MissingContextPrefix;

        return string.IsNullOrWhiteSpace(remainder[ContextPrefix.Length..])
            ? DynamicRoleFormat.EmptyNavigationPath
            : DynamicRoleFormat.WellFormed;
    }
}

/// <summary>
/// Outcome of classifying a role grant string via <see cref="DynamicRoleGrant.Classify"/>.
/// </summary>
public enum DynamicRoleFormat
{
    /// <summary>
    /// No dynamic-role qualifier prefix — a static role name or a predefined instance role.
    /// Nothing to validate.
    /// </summary>
    NotDynamic,

    /// <summary>A well-formed dynamic role: <c>$&lt;qualifier&gt;.$.context.&lt;path&gt;</c>.</summary>
    WellFormed,

    /// <summary>
    /// Dynamic-role intent, but the part after the qualifier does not open with the literal
    /// <c>$.context.</c> (including a case variant, which the runtime parser also rejects).
    /// </summary>
    MissingContextPrefix,

    /// <summary>Dynamic-role intent with <c>$.context.</c> but no navigation path after it.</summary>
    EmptyNavigationPath
}
