using System.Buffers;
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
/// Created by <c>TransitionAuthorizationManager.CreateEvaluatorAsync</c>, which performs the
/// asynchronous prefetch. When the evaluator carries no instance it degrades to
/// <see cref="TransitionAuthorizationManager.EvaluateRolesStatic"/>, because predefined and dynamic
/// grants have nothing to resolve against.
/// </para>
/// </summary>
internal sealed class RoleGrantEvaluator : IRoleGrantEvaluator
{
    private const string InstanceSegment = "Instance";
    private const string DataSegment = "Data";
    private const string TransitionSegment = "Transition";

    private readonly Instance? _instance;
    private readonly WorkflowDefinition? _workflow;
    private readonly AuthorizationRequestContext? _requestContext;
    private readonly InstanceTransition? _previousTransition;
    private readonly string? _actorUserName;
    private readonly string? _subjectUserName;

    /// <summary>
    /// The transition-independent part of the authorization context — <c>Instance</c> WITHOUT its
    /// <c>Data</c>, <c>Workflow</c>, <c>Body</c>, <c>Headers</c>, <c>QueryParameters</c>, <c>RouteValues</c> —
    /// built once per evaluator, lazily: a grant set with no dynamic grant never triggers a build.
    /// </summary>
    /// <remarks>
    /// It used to be one combined document per TRANSITION KEY, each carrying a full copy of the instance's
    /// latest data (written out, copied to an array and re-parsed into a pooled <see cref="JsonDocument"/>
    /// that was never disposed). A state with N transitions copied the data N times. Now
    /// <c>Instance.Data.*</c> is resolved against <see cref="_instanceData"/> directly and <c>Transition.*</c>
    /// against a four-field element per key, so the data is never copied at all. The root's and
    /// <c>Instance</c>'s property names are distinct case-insensitively, so routing a path on its leading
    /// segments selects exactly the node the case-insensitive walk over the old combined document reached.
    /// </remarks>
    private JsonElement? _baseContext;

    /// <summary>
    /// <c>instance.LatestData.Data</c> as the old combined document embedded it; null when the instance has
    /// no latest data (the old document embedded <c>{}</c>, which no path can resolve a value from).
    /// Captured with <see cref="_baseContext"/>.
    /// </summary>
    private JsonElement? _instanceData;

    /// <summary>
    /// The <c>Transition</c> node per transition key — memoized by key exactly as the combined document was.
    /// </summary>
    private Dictionary<string, JsonElement>? _transitionContexts;

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
        // is the side that resolves predefined and dynamic grants, and a dynamic grant builds the
        // authorization context. A refusal now skips that entirely.
        //
        // ── Phase 2: the ALLOW group, OR ────────────────────────────────────────────────────────
        //
        // Any one allow grant matching admits. A set with no allow grant at all is a blacklist — it
        // has already said everything it had to say in phase 1 — so it admits rather than refusing.
        //
        // ── Three-valued leaves (Kleene) ────────────────────────────────────────────────────────
        //
        // Both phases live in RoleGrantMatcher.Decide, shared with the static twin; this method only
        // says how ONE leaf matches. A caller with NO roles cannot be compared against a role-bound
        // leaf (static role, $role.): "nothing matched" is not evidence it is not the denied caller,
        // so the leaf is Unknown. A deny fires on Yes OR Unknown, an allow admits only on Yes — so a
        // role-less caller still cannot clear a role-bound deny, and inside an allOf an identity leaf
        // that is a proven No still rules the whole deny out (Unknown AND No = No).
        var noRoles = HasNoRoles(roles);
        return RoleGrantMatcher.Decide(grants, leafRole =>
        {
            if (noRoles && TransitionAuthorizationManager.IsRoleBound(leafRole))
                return GrantMatch.Unknown;
            return MatchesAnyRole(leafRole, roles, transition) ? GrantMatch.Yes : GrantMatch.No;
        });
    }

    /// <summary>
    /// Whether one leaf role matches anything the caller carries.
    /// </summary>
    /// <remarks>
    /// A predefined (<c>$InstanceStarter</c>) or identity-bound dynamic (<c>$user.</c>) leaf matches
    /// on the GRANT's side and answers the same for every caller role, so the first iteration decides
    /// it; only static leaves and <c>$role.</c> references actually vary. The loop is therefore
    /// bounded by how many roles a caller has, and short-circuits on the first hit.
    /// </remarks>
    private bool MatchesAnyRole(
        string leafRole,
        IReadOnlyList<string> roles,
        Transition? transition)
    {
        foreach (var role in roles)
        {
            if (IsMatch(leafRole, role, transition))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <see cref="NormalizeRoles"/> produced its role-less sentinel rather than real roles.
    /// </summary>
    private static bool HasNoRoles(IReadOnlyList<string> normalizedRoles)
        => normalizedRoles is [var only] && only.Length == 0;

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
    /// Resolves a single leaf role: predefined role, then dynamic context reference, then static comparison.
    /// </summary>
    private bool IsMatch(string grantRole, string normalizedRole, Transition? transition)
    {
        // 1. Predefined role check
        var predefinedResult = MatchPredefinedRole(
            grantRole, _instance!, _previousTransition, _actorUserName, _subjectUserName);
        if (predefinedResult.HasValue)
            return predefinedResult.Value;

        // 2. Dynamic context reference
        var dynamicGrant = DynamicRoleGrant.TryParse(grantRole);
        if (dynamicGrant != null)
        {
            return ResolveDynamicRoleMatch(
                dynamicGrant,
                path => ResolveContextPath(path, transition),
                normalizedRole,
                _actorUserName,
                _subjectUserName);
        }

        // 3. Static role comparison (OrdinalIgnoreCase)
        return string.Equals(grantRole, normalizedRole, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a <c>$.context.</c> navigation path to its values — the same values
    /// <see cref="ContextPathResolver.Resolve(JsonElement, string)"/> returned against the old combined
    /// per-transition document.
    /// </summary>
    private IReadOnlyList<string> ResolveContextPath(string navigationPath, Transition? transition)
    {
        // Built first and unconditionally, as the combined document was before any path was walked: an
        // instance whose latest data cannot be written fails here exactly as it failed there.
        var baseContext = GetBaseContext();

        var segments = ContextPathResolver.SplitSegments(navigationPath);
        if (segments.Length == 0)
            return [];

        if (segments.Length >= 2
            && string.Equals(segments[0], InstanceSegment, StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], DataSegment, StringComparison.OrdinalIgnoreCase))
        {
            return _instanceData is { } data
                ? ContextPathResolver.Resolve(data, segments, 2)
                : [];
        }

        if (string.Equals(segments[0], TransitionSegment, StringComparison.OrdinalIgnoreCase))
        {
            // A null transition was a JSON null in the combined document: nothing below it, no leaf value.
            return transition is null
                ? []
                : ContextPathResolver.Resolve(GetTransitionContext(transition), segments, 1);
        }

        return ContextPathResolver.Resolve(baseContext, segments, 0);
    }

    private JsonElement GetBaseContext()
    {
        if (_baseContext is { } built)
            return built;

        _instanceData = _instance?.LatestData?.Data.JsonElement;
        var element = BuildBaseContextElement(_instance, _instanceData, _workflow, _requestContext);
        _baseContext = element;
        return element;
    }

    private JsonElement GetTransitionContext(Transition transition)
    {
        _transitionContexts ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (_transitionContexts.TryGetValue(transition.Key, out var cached))
            return cached;

        var element = BuildTransitionElement(transition);
        _transitionContexts[transition.Key] = element;
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

        // $Everyone is every caller; nothing about the instance or the caller can make it No.
        if (PredefinedInstanceRoles.IsEveryone(grantRole))
            return true;

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
        Func<string, IReadOnlyList<string>> resolvePath,
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

        var values = resolvePath(navigationPath);
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
    /// Builds the transition-independent authorization context, structured to match the <c>$.context.*</c>
    /// path namespace used in dynamic role grants: <c>Instance</c> (without <c>Data</c>, which
    /// <see cref="ResolveContextPath"/> reads from the instance's own element), <c>Transition</c> as null
    /// (resolved per key by <see cref="GetTransitionContext"/>), <c>Workflow</c>, an empty <c>Body</c> (the
    /// request body is not available at authorization time), and the request context's dictionaries.
    /// <para>
    /// Parsed with <see cref="JsonElement.ParseValue"/>, which backs the element with an unpooled document:
    /// nothing is rented, so nothing has to be disposed when the evaluator goes out of scope.
    /// </para>
    /// </summary>
    private static JsonElement BuildBaseContextElement(
        Instance? instance,
        JsonElement? instanceData,
        WorkflowDefinition? workflow,
        AuthorizationRequestContext? requestContext)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
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
                // Data is not copied. An element that cannot be written (Undefined) failed the old
                // combined build here; keep failing it, at the same point, with the same exception.
                if (instanceData is { ValueKind: JsonValueKind.Undefined } unwritable)
                {
                    writer.WritePropertyName("Data");
                    unwritable.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("Instance");
            }

            // Transition: resolved per key, never from here.
            writer.WriteNull("Transition");

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

        return ParseUnpooled(buffer);
    }

    /// <summary>The <c>Transition</c> node of the authorization context for one transition.</summary>
    private static JsonElement BuildTransitionElement(Transition transition)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("Key", transition.Key);
            writer.WriteString("From", transition.From);
            writer.WriteString("Target", transition.Target);
            writer.WriteString("TriggerType", transition.TriggerType.ToString());
            writer.WriteEndObject();
        }

        return ParseUnpooled(buffer);
    }

    private static JsonElement ParseUnpooled(ArrayBufferWriter<byte> buffer)
    {
        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
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
