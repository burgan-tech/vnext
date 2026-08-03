using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The single role grant evaluation core. Holds everything an evaluation needs that is expensive to
/// obtain — the current user's actor/subject identity, the last completed manual transition, and the
/// dynamic-role authorization context — so that a batch of grant sets is evaluated with one round of I/O.
/// <para>
/// Created by <see cref="TransitionAuthorizationManager.CreateEvaluatorAsync"/>, which performs the
/// asynchronous prefetch. When the evaluator carries no instance it degrades to
/// <see cref="TransitionAuthorizationManager.EvaluateRolesStatic"/>, because predefined and dynamic
/// grants have nothing to resolve against.
/// </para>
/// </summary>
internal sealed class RoleGrantEvaluator : IRoleGrantEvaluator
{
    private const string NoTransitionCacheKey = "";

    private readonly Instance? _instance;
    private readonly WorkflowDefinition? _workflow;
    private readonly AuthorizationRequestContext? _requestContext;
    private readonly InstanceTransition? _previousTransition;
    private readonly string? _actorUserName;
    private readonly string? _subjectUserName;

    /// <summary>
    /// Authorization context elements memoized per transition key. Built lazily: a grant set with no
    /// dynamic grant never triggers a build, which matters because building serializes the instance's
    /// full latest data.
    /// </summary>
    private readonly Dictionary<string, JsonElement> _authContextCache = new(StringComparer.Ordinal);

    internal RoleGrantEvaluator(
        Instance? instance,
        WorkflowDefinition? workflow,
        AuthorizationRequestContext? requestContext,
        InstanceTransition? previousTransition,
        string? actorUserName,
        string? subjectUserName)
    {
        _instance = instance;
        _workflow = workflow;
        _requestContext = requestContext;
        _previousTransition = previousTransition;
        _actorUserName = actorUserName;
        _subjectUserName = subjectUserName;
    }

    /// <inheritdoc />
    public bool IsRoleAllowed(
        string? callerRole,
        IReadOnlyCollection<RoleGrant> grants,
        Transition? transition = null)
    {
        if (grants.Count == 0)
            return true; // No roles defined → allow

        if (_instance == null)
            return TransitionAuthorizationManager.EvaluateRolesStatic(callerRole, grants);

        var normalizedRole = callerRole?.Trim() ?? string.Empty;

        // Single pass: DENY wins wherever it appears, so a matching DENY short-circuits regardless of
        // position. An ALLOW match is only decisive once the whole set has been scanned for denials.
        var hasAllowGrant = false;
        var hasAllowMatch = false;

        foreach (var grant in grants)
        {
            if (grant.IsDeny)
            {
                if (IsMatch(grant, normalizedRole, transition))
                    return false;
            }
            else if (grant.IsAllow)
            {
                hasAllowGrant = true;
                if (!hasAllowMatch && IsMatch(grant, normalizedRole, transition))
                    hasAllowMatch = true;
            }
        }

        if (hasAllowMatch)
            return true;

        // Blacklist (deny-only) set: no ALLOW grant defined → allow when not explicitly denied.
        return !hasAllowGrant;
    }

    /// <inheritdoc />
    public bool IsAnyRoleAllowed(
        IReadOnlyCollection<string>? callerRoles,
        IReadOnlyCollection<RoleGrant> grants,
        Transition? transition = null)
    {
        if (grants.Count == 0)
            return true; // No roles defined → allow

        // No caller roles: still evaluate predefined/dynamic grants once.
        if (callerRoles is null || callerRoles.Count == 0)
            return IsRoleAllowed(null, grants, transition);

        foreach (var role in callerRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
                continue;
            if (IsRoleAllowed(role.Trim(), grants, transition))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a single grant: predefined role, then dynamic context reference, then static comparison.
    /// </summary>
    private bool IsMatch(RoleGrant grant, string normalizedRole, Transition? transition)
    {
        // 1. Predefined role check
        var predefinedResult = MatchPredefinedRole(
            grant.Role, _instance!, _previousTransition, _actorUserName, _subjectUserName);
        if (predefinedResult.HasValue)
            return predefinedResult.Value;

        // 2. Dynamic context reference
        var dynamicGrant = DynamicRoleGrant.TryParse(grant.Role);
        if (dynamicGrant != null)
        {
            return ResolveDynamicRoleMatch(
                dynamicGrant,
                () => GetAuthContext(transition),
                normalizedRole,
                _actorUserName,
                _subjectUserName);
        }

        // 3. Static role comparison (OrdinalIgnoreCase)
        return string.Equals(grant.Role, normalizedRole, StringComparison.OrdinalIgnoreCase);
    }

    private JsonElement GetAuthContext(Transition? transition)
    {
        var cacheKey = transition?.Key ?? NoTransitionCacheKey;
        if (_authContextCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var element = BuildAuthorizationContextElement(_instance, transition, _workflow, _requestContext);
        _authContextCache[cacheKey] = element;
        return element;
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
    /// <c>Body</c> is an empty object (the request body is not available at authorization time).
    /// </para>
    /// </summary>
    private static JsonElement BuildAuthorizationContextElement(
        Instance? instance,
        Transition? transition,
        WorkflowDefinition? workflow,
        AuthorizationRequestContext? requestContext)
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
}
