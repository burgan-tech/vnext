namespace BBT.Workflow.Definitions;

/// <summary>
/// Predefined system role names for instance-level authorization.
/// Actor roles (<c>$InstanceStarter</c>, <c>$PreviousUser</c>) are matched against <c>ICurrentUser.ActorUserName</c>.
/// BehalfOf roles (<c>$InstanceBehalfOfStarter</c>, <c>$PreviousBehalfOfUser</c>) are matched against <c>ICurrentUser.UserName</c>.
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
    /// Role resolved to the behalf-of subject who started the instance (Instance.CreatedByBehalfOf).
    /// When this role is in a grant: the subject on whose behalf the instance was started may perform the operation.
    /// Matching: CurrentUser.UserName (sub) is compared to Instance.CreatedByBehalfOf.
    /// </summary>
    public const string InstanceBehalfOfStarter = "$InstanceBehalfOfStarter";

    /// <summary>
    /// Role resolved to the behalf-of subject who triggered the last completed manual transition (InstanceTransition.CreatedByBehalfOf).
    /// When this role is in a grant: the subject who was represented during the previous manual transition may perform the operation.
    /// Matching: CurrentUser.UserName (sub) is compared to the last manual transition's CreatedByBehalfOf.
    /// Only manual transitions are considered; automatic/scheduled/event transitions are ignored.
    /// </summary>
    public const string PreviousBehalfOfUser = "$PreviousBehalfOfUser";

    /// <summary>
    /// Determines whether the given role is a predefined instance role.
    /// </summary>
    /// <param name="role">Role identifier to check.</param>
    /// <returns>True if the role is one of the four predefined instance roles.</returns>
    public static bool IsPredefinedRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return false;
        var normalized = role.Trim();
        return string.Equals(normalized, InstanceStarter, StringComparison.Ordinal)
            || string.Equals(normalized, PreviousUser, StringComparison.Ordinal)
            || string.Equals(normalized, InstanceBehalfOfStarter, StringComparison.Ordinal)
            || string.Equals(normalized, PreviousBehalfOfUser, StringComparison.Ordinal);
    }
}
