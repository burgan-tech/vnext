using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Encapsulates transition-level role grant evaluation (static roles + $InstanceStarter / $PreviousUser).
/// Used by AuthorizeAppService for single-transition checks and by InstanceQueryAppService for filtering available transitions.
/// </summary>
public interface ITransitionAuthorizationManager
{
    /// <summary>
    /// Evaluates whether the given role is allowed for the transition using transition.Roles.
    /// When instance is present, predefined roles ($InstanceStarter, $PreviousUser) are resolved and matched against current user.
    /// DENY always wins; if no DENY match, any ALLOW match yields true.
    /// </summary>
    /// <param name="workflow">The workflow definition (for context).</param>
    /// <param name="transition">The transition whose Roles are evaluated.</param>
    /// <param name="instance">Optional instance for resolving $InstanceStarter and $PreviousUser.</param>
    /// <param name="role">The caller's role to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the role is allowed for the transition; false otherwise.</returns>
    Task<bool> IsTransitionAllowedForRoleAsync(
        WorkflowDefinition workflow,
        Transition transition,
        Instance? instance,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters a list of transition keys to only those allowed for the given role.
    /// Uses the same evaluation as <see cref="IsTransitionAllowedForRoleAsync"/> per transition.
    /// </summary>
    /// <param name="workflow">The workflow definition.</param>
    /// <param name="currentState">Current state (used to resolve transition by key via workflow context).</param>
    /// <param name="instance">Optional instance for predefined role resolution.</param>
    /// <param name="transitionKeys">Candidate transition keys to filter.</param>
    /// <param name="role">The caller's role.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of transition keys that are allowed for the role.</returns>
    Task<IReadOnlyList<string>> FilterAuthorizedTransitionKeysAsync(
        WorkflowDefinition workflow,
        State currentState,
        Instance? instance,
        IReadOnlyList<string> transitionKeys,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether the given role is allowed for a set of role grants (e.g. function or state queryRoles).
    /// When instance is present, predefined roles ($InstanceStarter, $PreviousUser) are resolved.
    /// DENY always wins; if no DENY match, any ALLOW match yields true.
    /// </summary>
    Task<bool> IsRoleAllowedForGrantsAsync(
        string role,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance? instance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the effective caller roles list for schema field-level visibility.
    /// Returns static roles (ICurrentUser.Roles); when instance is present, adds $InstanceStarter if
    /// CurrentUser.ActorUserName equals Instance.CreatedBy, and $PreviousUser if it equals the last manual transition's CreatedBy.
    /// </summary>
    /// <param name="instance">Optional instance for resolving predefined roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of role strings to use when evaluating schema path visibility.</returns>
    Task<IReadOnlyList<string>> GetEffectiveCallerRolesForFieldVisibilityAsync(
        Instance? instance,
        CancellationToken cancellationToken = default);
}
