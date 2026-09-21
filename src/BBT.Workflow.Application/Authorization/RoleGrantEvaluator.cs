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
        => IsAnyRoleAllowed(callerRole is null ? null : [callerRole], grants, transition);

    /// <inheritdoc />
    public bool IsAnyRoleAllowed(
        IReadOnlyCollection<string>? callerRoles,
        IReadOnlyCollection<RoleGrant> grants,
        Transition? transition = null)
    {
        if (grants.Count == 0)
            return true; // No roles defined → allow

        var roles = NormalizeRoles(callerRoles);

        if (_instance == null)
            return TransitionAuthorizationManager.EvaluateRolesStatic(roles, grants);

        // ── Phase 1: the DENY group, AND ────────────────────────────────────────────────────────
        //
        // Every deny grant must hold, and a grant holds only while NOTHING the caller carries
        // matches it. One breach refuses outright — the caller's other roles cannot buy it back.
        //
        // This is the half that moved. It used to be evaluated per caller role inside a loop that
        // returned on the first role that was allowed, so a deny for role B was never reached once
        // role A had matched an allow: a caller holding [approver, blocked] passed. Measured on the
        // running lab, against the state function which shares this evaluator:
        //   roles=approver          -> [approve, cancel]
        //   roles=blocked           -> []
        //   roles=approver,blocked  -> [approve, cancel]   ← the deny did not veto
        //
        // Deny runs FIRST, and not only because a refusal is the cheaper answer: matching an allow
        // is the side that resolves predefined and dynamic grants, and a dynamic grant's context
        // build serializes the instance's full latest data. A refusal now skips that entirely.
        foreach (var grant in grants)
        {
            if (grant.IsDeny && MatchesAnyRole(grant, roles, transition))
                return false;
        }

        // ── Phase 2: the ALLOW group, OR ────────────────────────────────────────────────────────
        //
        // Any one allow grant matching any one of the caller's roles admits. A set with no allow
        // grant at all is a blacklist — it has already said everything it had to say in phase 1 —
        // so it admits here rather than falling through to a refusal.
        var hasAllowGrant = false;
        foreach (var grant in grants)
        {
            if (!grant.IsAllow)
                continue;

            hasAllowGrant = true;
            if (MatchesAnyRole(grant, roles, transition))
                return true;
        }

        return !hasAllowGrant;
    }

    /// <summary>
    /// Whether one grant matches anything the caller carries.
    /// </summary>
    /// <remarks>
    /// A predefined (<c>$InstanceStarter</c>) or identity-bound dynamic (<c>$user.</c>) grant matches
    /// on the GRANT's side and answers the same for every caller role, so the first iteration decides
    /// it; only static grants and <c>$role.</c> references actually vary. The loop is therefore
    /// bounded by how many roles a caller has, and short-circuits on the first hit.
    /// </remarks>
    private bool MatchesAnyRole(
        RoleGrant grant,
        IReadOnlyList<string> roles,
        Transition? transition)
    {
        foreach (var role in roles)
        {
            if (IsMatch(grant, role, transition))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Trims and drops blank roles. A caller with none is represented by a single empty role so
    /// predefined and dynamic grants are still evaluated exactly once — they resolve against the
    /// instance and the current user, not against a role name, and dropping the evaluation entirely
    /// would silently disable every <c>$InstanceStarter</c> grant for an unroled caller.
    /// </summary>
    private static IReadOnlyList<string> NormalizeRoles(IReadOnlyCollection<string>? callerRoles)
    {
        if (callerRoles is null || callerRoles.Count == 0)
            return [string.Empty];

        var normalized = new List<string>(callerRoles.Count);
        foreach (var role in callerRoles)
        {
            if (!string.IsNullOrWhiteSpace(role))
                normalized.Add(role.Trim());
        }

        return normalized.Count == 0 ? [string.Empty] : normalized;
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
                // Rendered the same way as Status above, deliberately: a grant that compares the two
                // must compare like with like, and Status' existing "Active (A)" rendering is a
                // shipped contract that cannot change under it.
                writer.WriteString("EffectiveStatus", instance.GetEffectiveStatus.ToString());
                // The bare CODE, unlike the two above. Status and EffectiveStatus carry a shipped
                // "Active (A)" rendering that cannot change under existing grants; Type is new, so
                // it gets the form a grant author actually wants to compare against —
                // $.context.Instance.Type == "S". Do not "fix" the inconsistency.
                writer.WriteString("Type", instance.Type.Code);
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
