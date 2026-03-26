using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Evaluates transition-level role grants (static + $InstanceStarter / $PreviousUser).
/// DENY always wins; if no DENY match, any ALLOW match yields allowed.
/// </summary>
public sealed class TransitionAuthorizationManager(
    ICurrentUser currentUser,
    IInstanceTransitionRepository instanceTransitionRepository) : ITransitionAuthorizationManager
{
    /// <inheritdoc />
    public async Task<bool> IsTransitionAllowedForRoleAsync(
        WorkflowDefinition workflow,
        Transition transition,
        Instance? instance,
        string? role,
        CancellationToken cancellationToken = default)
    {
        var roleGrants = transition.Roles;
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        if (instance != null)
            return await EvaluateRolesWithPredefinedAsync(role, roleGrants, instance, cancellationToken);

        return EvaluateRolesStatic(role, roleGrants);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FilterAuthorizedTransitionKeysAsync(
        WorkflowDefinition workflow,
        State currentState,
        Instance? instance,
        IReadOnlyList<string> transitionKeys,
        string? role,
        CancellationToken cancellationToken = default)
    {
        if (transitionKeys.Count == 0)
            return transitionKeys;

        var result = new List<string>();
        foreach (var key in transitionKeys)
        {
            var transition = workflow.FindTransitionInContext(key);
            if (transition == null)
                continue;
            var allowed = await IsTransitionAllowedForRoleAsync(workflow, transition, instance, role, cancellationToken);
            if (allowed)
                result.Add(key);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsRoleAllowedForGrantsAsync(
        string? role,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance? instance,
        CancellationToken cancellationToken = default)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow
        if (instance != null)
            return await EvaluateRolesWithPredefinedAsync(role, roleGrants, instance, cancellationToken);
        return EvaluateRolesStatic(role, roleGrants);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetEffectiveCallerRolesForFieldVisibilityAsync(
        Instance? instance,
        CancellationToken cancellationToken = default)
    {
        var roles = currentUser.Roles is { Length: > 0 }
            ? new List<string>(currentUser.Roles)
            : new List<string>();

        if (instance == null)
            return roles;

        var actorUserName = currentUser.ActorUserName?.Trim();
        if (!string.IsNullOrEmpty(actorUserName))
        {
            if (string.Equals(actorUserName, instance.CreatedBy?.Trim(), StringComparison.Ordinal))
                roles.Add(PredefinedInstanceRoles.InstanceStarter);

            var lastManual = await instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instance.Id, cancellationToken);
            var previousUserCreatedBy = lastManual?.CreatedBy?.Trim();
            if (!string.IsNullOrEmpty(previousUserCreatedBy) && string.Equals(actorUserName, previousUserCreatedBy, StringComparison.Ordinal))
                roles.Add(PredefinedInstanceRoles.PreviousUser);
        }

        return roles;
    }

    /// <summary>
    /// Evaluates role grants with resolution of predefined instance roles ($InstanceStarter, $PreviousUser).
    /// When role is null, only predefined role grants are evaluated (via ICurrentUser.ActorUserName); regular role grants yield no match.
    /// </summary>
    private async Task<bool> EvaluateRolesWithPredefinedAsync(
        string? role,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance instance,
        CancellationToken cancellationToken)
    {
        var normalizedRole = role?.Trim() ?? string.Empty;
        var currentActorUserName = currentUser.ActorUserName?.Trim();

        string? previousUserCreatedBy = null;
        if (roleGrants.Any(g => string.Equals(g.Role, PredefinedInstanceRoles.PreviousUser, StringComparison.Ordinal)))
            previousUserCreatedBy = (await instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instance.Id, cancellationToken))?.CreatedBy;

        bool IsMatch(RoleGrant g)
        {
            var resolvedValue = ResolvePredefinedRole(g.Role, instance, previousUserCreatedBy);
            if (resolvedValue != null)
                return !string.IsNullOrEmpty(currentActorUserName) && string.Equals(currentActorUserName, resolvedValue, StringComparison.Ordinal);
            return string.Equals(g.Role, normalizedRole, StringComparison.OrdinalIgnoreCase);
        }

        if (roleGrants.Any(g => g.IsDeny && IsMatch(g)))
            return false;

        if (roleGrants.Any(g => g.IsAllow && IsMatch(g)))
            return true;

        return false;
    }

    /// <summary>
    /// Resolves predefined role to the effective user identifier for comparison with CurrentUser.ActorUserName.
    /// </summary>
    private static string? ResolvePredefinedRole(string? grantRole, Instance instance, string? previousUserCreatedBy)
    {
        if (string.IsNullOrWhiteSpace(grantRole))
            return null;
        if (string.Equals(grantRole, PredefinedInstanceRoles.InstanceStarter, StringComparison.Ordinal))
            return instance.CreatedBy;
        if (string.Equals(grantRole, PredefinedInstanceRoles.PreviousUser, StringComparison.Ordinal))
            return previousUserCreatedBy;
        return null;
    }

    /// <summary>
    /// Evaluates role against role grants (static only). DENY always wins; else any ALLOW match → true.
    /// When role is null, no regular role grants match; only the grant count check applies (empty grants → allow).
    /// Used by transition/function authorization and by schema field-level visibility.
    /// </summary>
    public static bool EvaluateRolesStatic(string? role, IReadOnlyCollection<RoleGrant> roleGrants)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow
        var normalizedRole = role?.Trim() ?? string.Empty;
        foreach (var g in roleGrants)
        {
            if (string.Equals(g.Role, normalizedRole, StringComparison.OrdinalIgnoreCase) && g.IsDeny)
                return false;
        }
        foreach (var g in roleGrants)
        {
            if (string.Equals(g.Role, normalizedRole, StringComparison.OrdinalIgnoreCase) && g.IsAllow)
                return true;
        }
        return false;
    }
}
