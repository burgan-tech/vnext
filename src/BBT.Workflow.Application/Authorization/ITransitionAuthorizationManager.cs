using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Encapsulates transition-level role grant evaluation: static roles, predefined instance roles, and dynamic context references.
/// Used by AuthorizeAppService for single-transition checks and by InstanceQueryAppService for filtering available transitions.
/// <para>
/// Role types supported in <see cref="RoleGrant.Role"/>:
/// <list type="bullet">
///   <item>Static roles: e.g. <c>morph-idm.maker</c> — matched case-insensitively against <c>ICurrentUser.Roles</c>.</item>
///   <item><c>$InstanceStarter</c> — matched against <c>Instance.CreatedBy</c> via <c>ICurrentUser.ActorUserName</c>.</item>
///   <item><c>$PreviousUser</c> — matched against last manual <c>InstanceTransition.CreatedBy</c> via <c>ICurrentUser.ActorUserName</c>.</item>
///   <item><c>$InstanceBehalfOfStarter</c> — matched against <c>Instance.CreatedByBehalfOf</c> via <c>ICurrentUser.UserName</c>.</item>
///   <item><c>$PreviousBehalfOfUser</c> — matched against last manual <c>InstanceTransition.CreatedByBehalfOf</c> via <c>ICurrentUser.UserName</c>.</item>
///   <item>Dynamic: <c>$user.$.context.&lt;path&gt;</c>, <c>$userBehalfOf.$.context.&lt;path&gt;</c>, <c>$role.$.context.&lt;path&gt;</c>
///     — value resolved from the authorization context (Instance, Transition, Workflow) at evaluation time.</item>
/// </list>
/// </para>
/// </summary>
public interface ITransitionAuthorizationManager
{
    /// <summary>
    /// Evaluates whether the given role is allowed for the transition using transition.Roles.
    /// When instance is present, predefined and dynamic role grants are resolved and matched against current user.
    /// When role is null, only predefined/dynamic role grants are evaluated; regular role grants yield no match.
    /// DENY always wins; if no DENY match, any ALLOW match yields true.
    /// </summary>
    /// <param name="workflow">The workflow definition (provides Workflow context for dynamic role evaluation).</param>
    /// <param name="transition">The transition whose Roles are evaluated (provides Transition context for dynamic role evaluation).</param>
    /// <param name="instance">Optional instance for resolving predefined and dynamic roles.</param>
    /// <param name="role">The caller's role to check. Null is allowed; predefined/dynamic roles are still evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the role is allowed for the transition; false otherwise.</returns>
    Task<bool> IsTransitionAllowedForRoleAsync(
        WorkflowDefinition workflow,
        Transition transition,
        Instance? instance,
        string? role,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters a list of transition keys to only those allowed for the given role.
    /// Uses the same evaluation as <see cref="IsTransitionAllowedForRoleAsync"/> per transition.
    /// When role is null, only predefined/dynamic role grants are evaluated; transitions with no roles pass through.
    /// </summary>
    /// <param name="workflow">The workflow definition.</param>
    /// <param name="currentState">Current state (used to resolve transition by key via workflow context).</param>
    /// <param name="instance">Optional instance for predefined and dynamic role resolution.</param>
    /// <param name="transitionKeys">Candidate transition keys to filter.</param>
    /// <param name="role">The caller's role. Null is allowed; predefined/dynamic roles are still evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of transition keys that are allowed for the role.</returns>
    Task<IReadOnlyList<string>> FilterAuthorizedTransitionKeysAsync(
        WorkflowDefinition workflow,
        State currentState,
        Instance? instance,
        IReadOnlyList<string> transitionKeys,
        string? role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether the given role is allowed for a set of role grants (e.g. function or state queryRoles).
    /// When instance is present, predefined and dynamic role grants are resolved.
    /// When role is null, only predefined/dynamic role grants are evaluated.
    /// DENY always wins; if no DENY match, any ALLOW match yields true.
    /// </summary>
    Task<bool> IsRoleAllowedForGrantsAsync(
        string? role,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance? instance,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the effective caller roles list for schema field-level visibility.
    /// Returns static roles (ICurrentUser.Roles); when instance is present, adds applicable predefined roles:
    /// <c>$InstanceStarter</c>, <c>$PreviousUser</c> (matched via ActorUserName),
    /// <c>$InstanceBehalfOfStarter</c>, <c>$PreviousBehalfOfUser</c> (matched via UserName).
    /// </summary>
    /// <param name="instance">Optional instance for resolving predefined roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of role strings to use when evaluating schema path visibility.</returns>
    Task<IReadOnlyList<string>> GetEffectiveCallerRolesForFieldVisibilityAsync(
        Instance? instance,
        CancellationToken cancellationToken = default);
}
