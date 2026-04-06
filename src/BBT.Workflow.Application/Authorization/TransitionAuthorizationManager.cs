using System.Text.Json;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Evaluates transition-level role grants (static + predefined + dynamic context references).
/// DENY always wins; if no DENY match, any ALLOW match yields allowed.
/// <para>
/// Predefined actor roles ($InstanceStarter, $PreviousUser) are matched against <c>ICurrentUser.ActorUserName</c>.
/// Predefined behalf-of roles ($InstanceBehalfOfStarter, $PreviousBehalfOfUser) are matched against <c>ICurrentUser.UserName</c>.
/// Dynamic roles ($user, $userBehalfOf, $role) resolve values from the authorization context via a ScriptContext-compatible path.
/// </para>
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
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var roleGrants = transition.Roles;
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        if (instance != null)
            return await EvaluateRolesWithPredefinedAsync(role, roleGrants, instance, cancellationToken, transition, workflow, requestContext);

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
            var allowed = await IsTransitionAllowedForRoleAsync(workflow, transition, instance, role, cancellationToken: cancellationToken);
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
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow
        if (instance != null)
            return await EvaluateRolesWithPredefinedAsync(role, roleGrants, instance, cancellationToken, requestContext: requestContext);
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
        var subjectUserName = currentUser.UserName?.Trim();

        // Single repository fetch used by both actor and behalf-of checks
        InstanceTransition? lastManual = null;
        if (!string.IsNullOrEmpty(actorUserName) || !string.IsNullOrEmpty(subjectUserName))
            lastManual = await instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instance.Id, cancellationToken);

        // Actor-based predefined roles
        if (!string.IsNullOrEmpty(actorUserName))
        {
            if (string.Equals(actorUserName, instance.CreatedBy?.Trim(), StringComparison.Ordinal))
                roles.Add(PredefinedInstanceRoles.InstanceStarter);

            var previousUserCreatedBy = lastManual?.CreatedBy?.Trim();
            if (!string.IsNullOrEmpty(previousUserCreatedBy) && string.Equals(actorUserName, previousUserCreatedBy, StringComparison.Ordinal))
                roles.Add(PredefinedInstanceRoles.PreviousUser);
        }

        // BehalfOf-based predefined roles
        if (!string.IsNullOrEmpty(subjectUserName))
        {
            if (string.Equals(subjectUserName, instance.CreatedByBehalfOf?.Trim(), StringComparison.Ordinal))
                roles.Add(PredefinedInstanceRoles.InstanceBehalfOfStarter);

            var previousBehalfOf = lastManual?.CreatedByBehalfOf?.Trim();
            if (!string.IsNullOrEmpty(previousBehalfOf) && string.Equals(subjectUserName, previousBehalfOf, StringComparison.Ordinal))
                roles.Add(PredefinedInstanceRoles.PreviousBehalfOfUser);
        }

        return roles;
    }

    /// <summary>
    /// Evaluates role grants with resolution of predefined instance roles and dynamic context references.
    /// When role is null, only predefined/dynamic role grants are evaluated; regular role grants yield no match.
    /// </summary>
    private async Task<bool> EvaluateRolesWithPredefinedAsync(
        string? role,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance instance,
        CancellationToken cancellationToken,
        Transition? transition = null,
        WorkflowDefinition? workflow = null,
        AuthorizationRequestContext? requestContext = null)
    {
        var normalizedRole = role?.Trim() ?? string.Empty;
        var currentActorUserName = currentUser.ActorUserName?.Trim();
        var currentSubjectUserName = currentUser.UserName?.Trim();

        // Fetch previous transition only if needed
        InstanceTransition? previousTransition = null;
        if (roleGrants.Any(g =>
            string.Equals(g.Role, PredefinedInstanceRoles.PreviousUser, StringComparison.Ordinal) ||
            string.Equals(g.Role, PredefinedInstanceRoles.PreviousBehalfOfUser, StringComparison.Ordinal)))
        {
            previousTransition = await instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instance.Id, cancellationToken);
        }

        // Lazy-built authorization context element for dynamic roles
        JsonElement? authContext = null;
        JsonElement GetAuthContext()
        {
            authContext ??= BuildAuthorizationContextElement(instance, transition, workflow, requestContext);
            return authContext.Value;
        }

        bool IsMatch(RoleGrant g)
        {
            // 1. Predefined role check
            var predefinedResult = MatchPredefinedRole(g.Role, instance, previousTransition, currentActorUserName, currentSubjectUserName);
            if (predefinedResult.HasValue)
                return predefinedResult.Value;

            // 2. Dynamic context reference
            var dynamicGrant = DynamicRoleGrant.TryParse(g.Role);
            if (dynamicGrant != null)
                return ResolveDynamicRoleMatch(dynamicGrant, GetAuthContext, normalizedRole, currentActorUserName, currentSubjectUserName);

            // 3. Static role comparison (OrdinalIgnoreCase)
            return string.Equals(g.Role, normalizedRole, StringComparison.OrdinalIgnoreCase);
        }

        if (roleGrants.Any(g => g.IsDeny && IsMatch(g)))
            return false;

        if (roleGrants.Any(g => g.IsAllow && IsMatch(g)))
            return true;

        return false;
    }

    /// <summary>
    /// Matches a predefined role against the current user and instance/transition data.
    /// Returns true/false for matched predefined roles; returns null if not a predefined role.
    /// </summary>
    private static bool? MatchPredefinedRole(
        string? grantRole,
        Instance instance,
        InstanceTransition? previousTransition,
        string? actorUserName,
        string? subjectUserName)
    {
        if (string.IsNullOrWhiteSpace(grantRole))
            return null;

        if (string.Equals(grantRole, PredefinedInstanceRoles.InstanceStarter, StringComparison.Ordinal))
            return !string.IsNullOrEmpty(actorUserName) &&
                   string.Equals(actorUserName, instance.CreatedBy?.Trim(), StringComparison.Ordinal);

        if (string.Equals(grantRole, PredefinedInstanceRoles.PreviousUser, StringComparison.Ordinal))
        {
            var prevCreatedBy = previousTransition?.CreatedBy?.Trim();
            return !string.IsNullOrEmpty(actorUserName) &&
                   !string.IsNullOrEmpty(prevCreatedBy) &&
                   string.Equals(actorUserName, prevCreatedBy, StringComparison.Ordinal);
        }

        if (string.Equals(grantRole, PredefinedInstanceRoles.InstanceBehalfOfStarter, StringComparison.Ordinal))
            return !string.IsNullOrEmpty(subjectUserName) &&
                   string.Equals(subjectUserName, instance.CreatedByBehalfOf?.Trim(), StringComparison.Ordinal);

        if (string.Equals(grantRole, PredefinedInstanceRoles.PreviousBehalfOfUser, StringComparison.Ordinal))
        {
            var prevBehalfOf = previousTransition?.CreatedByBehalfOf?.Trim();
            return !string.IsNullOrEmpty(subjectUserName) &&
                   !string.IsNullOrEmpty(prevBehalfOf) &&
                   string.Equals(subjectUserName, prevBehalfOf, StringComparison.Ordinal);
        }

        return null; // Not a predefined role
    }

    /// <summary>
    /// Resolves a dynamic role grant against the authorization context and compares to the current user.
    /// </summary>
    private static bool ResolveDynamicRoleMatch(
        DynamicRoleGrant grant,
        Func<JsonElement> getAuthContext,
        string normalizedCallerRole,
        string? actorUserName,
        string? subjectUserName)
    {
        const string contextPrefix = "$.context.";
        if (!grant.ContextPath.StartsWith(contextPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var navigationPath = grant.ContextPath[contextPrefix.Length..];
        if (string.IsNullOrWhiteSpace(navigationPath))
            return false;

        var values = ContextPathResolver.Resolve(getAuthContext(), navigationPath);
        if (values.Count == 0)
            return false;

        return grant.Qualifier switch
        {
            DynamicRoleQualifier.User =>
                !string.IsNullOrEmpty(actorUserName) &&
                values.Any(v => string.Equals(v, actorUserName, StringComparison.Ordinal)),

            DynamicRoleQualifier.UserBehalfOf =>
                !string.IsNullOrEmpty(subjectUserName) &&
                values.Any(v => string.Equals(v, subjectUserName, StringComparison.Ordinal)),

            DynamicRoleQualifier.Role =>
                values.Any(v => string.Equals(v, normalizedCallerRole, StringComparison.OrdinalIgnoreCase)),

            _ => false
        };
    }

    /// <summary>
    /// Builds a <see cref="JsonElement"/> representing the authorization context,
    /// structured to match the <c>$.context.*</c> path namespace used in dynamic role grants.
    /// <para>
    /// Includes <c>Instance</c>, <c>Transition</c>, <c>Workflow</c> when available.
    /// <c>Body</c> and <c>Headers</c> are empty objects (not available at authorization time).
    /// </para>
    /// </summary>
    private static JsonElement BuildAuthorizationContextElement(
        Instance? instance,
        Transition? transition,
        WorkflowDefinition? workflow,
        AuthorizationRequestContext? requestContext = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Instance
            if (instance != null)
            {
                writer.WritePropertyName("Instance");
                writer.WriteStartObject();
                writer.WriteString("Id", instance.Id.ToString());
                writer.WriteString("Key", instance.Key);
                writer.WriteString("Flow", instance.Flow);
                writer.WriteString("FlowVersion", instance.FlowVersion);
                writer.WriteString("Status", instance.Status.ToString());
                writer.WriteString("CurrentState", instance.CurrentState);
                writer.WriteString("EffectiveState", instance.EffectiveState);
                writer.WriteString("EffectiveStateType", instance.EffectiveStateType?.ToString());
                writer.WriteString("EffectiveStateSubType", instance.EffectiveStateSubType?.ToString());
                writer.WriteString("CreatedBy", instance.CreatedBy);
                writer.WriteString("CreatedByBehalfOf", instance.CreatedByBehalfOf);
                writer.WriteString("ModifiedBy", instance.ModifiedBy);
                writer.WriteString("ModifiedByBehalfOf", instance.ModifiedByBehalfOf);
                writer.WritePropertyName("Data");
                var dataElement = instance.LatestData?.Data.JsonElement
                    ?? JsonDocument.Parse("{}").RootElement;
                dataElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("Instance");
            }

            // Transition
            if (transition != null)
            {
                writer.WritePropertyName("Transition");
                writer.WriteStartObject();
                writer.WriteString("Key", transition.Key);
                writer.WriteString("From", transition.From);
                writer.WriteString("Target", transition.Target);
                writer.WriteString("TriggerType", transition.TriggerType.ToString());
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("Transition");
            }

            // Workflow
            if (workflow != null)
            {
                writer.WritePropertyName("Workflow");
                writer.WriteStartObject();
                writer.WriteString("Key", workflow.Key);
                writer.WriteString("Domain", workflow.Domain);
                writer.WriteString("Flow", workflow.Flow);
                writer.WriteString("Version", workflow.Version);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("Workflow");
            }

            // Body: empty (request body is not available at authorization time)
            writer.WriteStartObject("Body");
            writer.WriteEndObject();

            // Headers, QueryParameters, RouteValues: from request context when available
            WriteStringDictionary(writer, "Headers", requestContext?.Headers);
            WriteStringDictionary(writer, "QueryParameters", requestContext?.QueryParameters);
            WriteStringDictionary(writer, "RouteValues", requestContext?.RouteValues);

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement;
    }

    private static void WriteStringDictionary(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyDictionary<string, string?>? dict)
    {
        writer.WriteStartObject(propertyName);
        if (dict != null)
        {
            foreach (var (key, value) in dict)
                writer.WriteString(key, value);
        }
        writer.WriteEndObject();
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
