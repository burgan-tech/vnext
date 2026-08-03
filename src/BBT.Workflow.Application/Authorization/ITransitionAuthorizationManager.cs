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
    /// Creates a batch-scoped <see cref="IRoleGrantEvaluator"/>: performs the instance-bound prefetch once,
    /// then evaluates any number of grant sets synchronously. Use this instead of calling the per-check
    /// methods in a loop whenever many grant sets are evaluated for the same instance — schema field paths,
    /// the transitions available from a state, the instances of a list.
    /// </summary>
    /// <param name="instance">
    /// The instance predefined and dynamic grants resolve against. When null the evaluator degrades to
    /// static comparison only.
    /// </param>
    /// <param name="workflow">Optional workflow supplying the <c>$.context.Workflow.*</c> namespace.</param>
    /// <param name="requestContext">
    /// Optional request context supplying the <c>$.context.Headers.*</c>, <c>$.context.QueryParameters.*</c>
    /// and <c>$.context.RouteValues.*</c> namespaces. Omitting it makes those namespaces empty, so dynamic
    /// grants that reference them can never match.
    /// </param>
    /// <param name="grantsForPrefetchHint">
    /// Every grant the returned evaluator will be asked about. Only used to decide whether the previous
    /// manual transition must be loaded, so it must cover the whole batch: a <c>$PreviousUser</c> or
    /// <c>$PreviousBehalfOfUser</c> grant that is evaluated but was not in the hint can never match.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IRoleGrantEvaluator> CreateEvaluatorAsync(
        Instance? instance,
        WorkflowDefinition? workflow,
        AuthorizationRequestContext? requestContext,
        IEnumerable<RoleGrant> grantsForPrefetchHint,
        CancellationToken cancellationToken = default);

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
    /// <param name="requestContext">Optional request context for <c>$.context.Headers/QueryParameters/RouteValues</c>.</param>
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
    /// <param name="requestContext">
    /// Optional request context for <c>$.context.Headers/QueryParameters/RouteValues</c>. Pass the same context
    /// the <c>authorize</c> function passes, otherwise those namespaces are empty here, a transition guarded by
    /// a dynamic grant reading them silently never matches, and discovery drops a transition that
    /// <c>authorize</c> reports as allowed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of transition keys that are allowed for the role.</returns>
    Task<IReadOnlyList<string>> FilterAuthorizedTransitionKeysAsync(
        WorkflowDefinition workflow,
        State currentState,
        Instance? instance,
        IReadOnlyList<string> transitionKeys,
        string? role,
        AuthorizationRequestContext? requestContext = null,
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
    /// Evaluates a set of role grants against ALL of the caller's roles (multi-role: any allowed → allow).
    /// No grants → allow. When callerRoles is null/empty only predefined/dynamic grants are evaluated.
    /// DENY wins within each role evaluation. Reused for custom function <c>Roles</c> and state queryRoles.
    /// </summary>
    Task<bool> IsAnyRoleAllowedForGrantsAsync(
        IReadOnlyCollection<string>? callerRoles,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance? instance,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates state-based query visibility for the caller. Resolves the effective grants —
    /// the instance's effective-state <c>queryRoles</c> when present, otherwise <c>workflow.QueryRoles</c> —
    /// and evaluates the caller's roles against them (multi-role: any allowed → allow). No grants → allow.
    /// Used by the state/data/view/schema instance functions to gate access; predefined and dynamic role
    /// grants are honored via the instance and <paramref name="requestContext"/>.
    /// </summary>
    Task<bool> IsQueryAllowedAsync(
        WorkflowDefinition workflow,
        Instance instance,
        IReadOnlyCollection<string>? callerRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);
}
