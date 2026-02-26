namespace BBT.Workflow.Definitions;

/// <summary>
/// Predefined system role names for instance-level authorization.
/// These roles are resolved from instance/transition audit data; matching is done against CurrentUser.ActorUserName.
/// </summary>
public static class PredefinedInstanceRoles
{
    /// <summary>
    /// Role resolved to the user who started the instance (Instance.CreatedBy).
    /// When this role is in a grant: the same user who started the instance may perform the operation.
    /// Matching: CurrentUser.ActorUserName is compared to Instance.CreatedBy.
    /// </summary>
    public const string InstanceStarter = "$InstanceStarter";

    /// <summary>
    /// Role resolved to the user who created the last completed manual transition (InstanceTransition.CreatedBy).
    /// When this role is in a grant: the user who performed the previous manual transition may perform the operation.
    /// Matching: CurrentUser.ActorUserName is compared to the last manual transition's CreatedBy.
    /// Only manual transitions are considered; automatic/scheduled/event transitions are ignored.
    /// </summary>
    public const string PreviousUser = "$PreviousUser";

    /// <summary>
    /// Determines whether the given role is a predefined instance role.
    /// </summary>
    /// <param name="role">Role identifier to check.</param>
    /// <returns>True if the role is InstanceStarter or PreviousUser.</returns>
    public static bool IsPredefinedRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return false;
        var normalized = role.Trim();
        return string.Equals(normalized, InstanceStarter, StringComparison.Ordinal)
            || string.Equals(normalized, PreviousUser, StringComparison.Ordinal);
    }
}
