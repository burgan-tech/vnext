using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.HumanTask;

/// <inheritdoc />
public sealed class HumanTaskLeafResolver(
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    ICurrentSchema currentSchema,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    IHumanTaskLeafGateway gateway,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<HumanTaskLeafResolver> logger) : IHumanTaskLeafResolver
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
        string domain,
        string flow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.InstanceIds.Count == 0)
            return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok([]);

        if (request.RemainingDepth <= 0)
        {
            logger.HumanTaskDescentDepthExceeded(domain, flow, request.InstanceIds.Count);
            return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok(
                [.. request.InstanceIds.Select(id => HumanTaskLeafResult.Dropped(id, "depth-exceeded"))]);
        }

        var workflowResult = await componentCacheStore.GetFlowAsync(
            domain, flow, request.FlowVersion, cancellationToken);

        if (!workflowResult.IsSuccess || workflowResult.Value is null)
        {
            logger.HumanTaskWorkflowDropped(flow, domain);
            return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok(
                [.. request.InstanceIds.Select(id => HumanTaskLeafResult.Dropped(id, "flow-unresolved"))]);
        }

        var workflow = workflowResult.Value;

        List<Instance> instances;
        using (currentSchema.Change(flow))
        {
            instances = await instanceRepository.GetForHumanTaskDescentAsync(
                request.InstanceIds, cancellationToken);
        }

        var byId = instances.ToDictionary(i => i.Id);
        var results = new List<HumanTaskLeafResult>(request.InstanceIds.Count);

        // Leaf-side authorization for this level, timed as ONE span rather than one per leaf: a
        // branch can carry hundreds of candidates and the cardinality would swamp the trace for
        // what a count already says. Opened lazily, because a level that only descends authorizes
        // nothing and should leave no span at all.
        Activity? authorize = null;
        var leaves = 0;
        var authorized = 0;

        // Everything that must descend, keyed by the hop it takes. One group is one call, whether
        // the far side is this process or another domain.
        var hops = new Dictionary<(string Domain, string Flow, string? Version), List<(Guid Ancestor, Guid Child)>>();

        foreach (var instanceId in request.InstanceIds)
        {
            if (!byId.TryGetValue(instanceId, out var instance))
            {
                // READ COMMITTED permits deletion between selection and this load.
                results.Add(HumanTaskLeafResult.Dropped(instanceId, "instance-missing"));
                continue;
            }

            var activeSubFlow = instance.Subflow;
            if (activeSubFlow is null)
            {
                authorize ??= InstanceReadActivityHelper.StartHumanTaskAuthorize(flow);
                leaves++;

                var leafResult = await ResolveLeafAsync(
                    domain, flow, instance, workflow, request, cancellationToken);

                if (leafResult.Authorized) authorized++;
                results.Add(leafResult);
                continue;
            }

            var key = (activeSubFlow.SubFlowDomain, activeSubFlow.SubFlowName, activeSubFlow.SubFlowVersion);
            if (!hops.TryGetValue(key, out var members))
                hops[key] = members = [];
            members.Add((instanceId, activeSubFlow.SubFlowInstanceId));
        }

        if (authorize is not null)
        {
            authorize.SetTag(TelemetryConstants.TagNames.HumanTaskLeaves, leaves);
            authorize.SetTag(TelemetryConstants.TagNames.HumanTaskAuthorized, authorized);
            // Closed before the hops: the descent's own spans belong beside this one, not inside it.
            authorize.Dispose();
        }

        foreach (var (hop, members) in hops)
        {
            var childRequest = new HumanTaskLeafRequest
            {
                InstanceIds = [.. members.Select(m => m.Child)],
                CallerRoles = request.CallerRoles,
                Headers = request.Headers,
                RemainingDepth = request.RemainingDepth - 1,
                FlowVersion = hop.Version
            };

            // The existing Subflow.Descend span, one per HOP rather than per instance — the hop is
            // the unit of work here, and the transport tag comes from the same IsDomainMatch
            // predicate the gateway routes on. Before this the human-task path produced no descent
            // span at all, so a cross-domain hop was invisible.
            using var descent = InstanceReadActivityHelper.StartDescendScope(
                runtimeInfoProvider,
                hop.Domain,
                hop.Flow,
                targetInstanceId: null,
                parentInstanceId: null,
                function: InstanceReadKinds.HumanTasks);

            descent.Activity?.SetTag(TelemetryConstants.TagNames.HumanTaskHopSize, members.Count);

            var childResult = await gateway.ResolveAsync(
                hop.Domain, hop.Flow, childRequest, cancellationToken);

            if (!childResult.IsSuccess)
            {
                // A failed hop is an incomplete answer, not an empty one. It must not fail the whole
                // list — one unreachable partner domain would blank a banker's inbox — but it must
                // be visible, so every member is reported as dropped with its reason.
                logger.HumanTaskDescentHopFailed(
                    hop.Domain, hop.Flow, childResult.Error.Message);
                results.AddRange(members.Select(m => HumanTaskLeafResult.Dropped(m.Ancestor, "hop-failed")));
                continue;
            }

            // Map the child's answers back onto the ancestor ids this level was asked about: the
            // client knows the root, not the subflow, so identity never travels down.
            var childById = childResult.Value!.ToDictionary(r => r.InstanceId);
            foreach (var (ancestor, child) in members)
            {
                results.Add(childById.TryGetValue(child, out var answer)
                    ? new HumanTaskLeafResult
                    {
                        InstanceId = ancestor,
                        Resolved = answer.Resolved,
                        Authorized = answer.Authorized,
                        Title = answer.Title,
                        Description = answer.Description,
                        LeafDomain = answer.LeafDomain,
                        LeafFlow = answer.LeafFlow,
                        LeafState = answer.LeafState,
                        DropReason = answer.DropReason
                    }
                    : HumanTaskLeafResult.Dropped(ancestor, "child-missing"));
            }
        }

        return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok(results);
    }

    /// <summary>
    /// The instance is the leaf: decide whether the caller may SEE it, then read its humanTask text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is the state's <c>queryRoles</c>, resolved leaf-side, because the question this list
    /// answers is <b>"which human tasks am I responsible for?"</b> — a visibility question. Whether a
    /// particular button is offered is settled later, when the client opens the instance and the
    /// state function runs; deriving visibility from actionability conflates the two.
    /// </para>
    /// <para>
    /// It used to authorize <b>transitions</b>: an OR over everything
    /// <c>GetAvailableUserTransitionKeys</c> returned for the state. That was wrong three ways.
    /// It made this a fourth authorization surface, and one that did not apply the
    /// <c>availableIn</c> per-state role narrowing the state function applies — so the list could be
    /// MORE permissive than the screen it leads to. It let <c>cancel</c> be a back door, because the
    /// well-known transitions are appended from every state and an empty grant set allows, so a
    /// role-less <c>cancel</c> authorized every caller for every instance. And it produced a rule no
    /// domain team could state in a sentence. Reading <c>queryRoles</c> converges this list onto the
    /// very gate the client hits next.
    /// </para>
    /// <para>
    /// Precedence, highest first: the parent's stamped state override → the leaf state's own
    /// <c>queryRoles</c> → the leaf workflow's root <c>queryRoles</c>. The first is what makes
    /// <c>subflow.state_role_overrides</c> live at a leaf, where the parent-side reader cannot reach.
    /// </para>
    /// </remarks>
    private async Task<HumanTaskLeafResult> ResolveLeafAsync(
        string domain,
        string flow,
        Instance instance,
        Definitions.Workflow workflow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken)
    {
        var stateResult = workflow.GetState(instance.GetCurrentState);
        if (!stateResult.IsSuccess || stateResult.Value is null)
        {
            logger.HumanTaskStateUnresolved(instance.Id, instance.GetCurrentState, workflow.Key);
            return HumanTaskLeafResult.Dropped(instance.Id, "state-unresolved");
        }

        var state = stateResult.Value;
        var queryRoles = ResolveQueryRoles(instance, state, workflow);

        // Fail closed. An empty grant set ALLOWS everywhere else in the runtime, and letting that
        // default through here would publish every human task of every flow that has not declared
        // queryRoles to every caller — measured at 8 of the 10 example flows that have a human state.
        // A task list is the one read surface where "no rule authored" must not mean "everyone", so
        // an undeclared state is dropped and counted instead, loudly enough to drive the migration.
        if (queryRoles.Count == 0)
        {
            logger.HumanTaskQueryRolesUndeclared(instance.Id, flow, state.Key, domain);
            return HumanTaskLeafResult.Dropped(instance.Id, "query-roles-undeclared");
        }

        var requestContext = new AuthorizationRequestContext(request.Headers);

        var evaluator = await transitionAuthorizationManager.CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            queryRoles,
            cancellationToken);

        var callerRoles = request.CallerRoles as string[] ?? [.. request.CallerRoles];

        if (!evaluator.IsAnyRoleAllowed(callerRoles, queryRoles))
            return NotAuthorized(instance, domain, flow, state.Key);

        var (title, description) = ReadHumanTask(instance);

        return new HumanTaskLeafResult
        {
            InstanceId = instance.Id,
            Resolved = true,
            Authorized = true,
            Title = title,
            Description = description,
            LeafDomain = domain,
            LeafFlow = flow,
            LeafState = state.Key
        };
    }

    /// <summary>
    /// The grants that decide whether this caller may see this leaf.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TransitionAuthorizationManager.IsQueryAllowedAsync</c>'s state-then-workflow
    /// resolution, with the stamped parent override in front of both — the one step that reader
    /// cannot take, because it resolves overrides from a parent that a leaf does not have.
    /// </remarks>
    private static IReadOnlyCollection<RoleGrant> ResolveQueryRoles(
        Instance instance,
        State state,
        Definitions.Workflow workflow)
    {
        var stamped = BBT.Workflow.Authorization.SubFlowStateOverrideReader.TryReadQueryRoles(instance, state.Key);
        if (stamped is { Count: > 0 })
            return stamped;

        return state.QueryRoles is { Count: > 0 } ? state.QueryRoles : workflow.QueryRoles;
    }

    private static HumanTaskLeafResult NotAuthorized(Instance instance, string domain, string flow, string state) =>
        new()
        {
            InstanceId = instance.Id,
            Resolved = true,
            Authorized = false,
            LeafDomain = domain,
            LeafFlow = flow,
            LeafState = state
        };

    /// <summary>Reads <c>humanTask.title</c> / <c>.description</c> from the leaf's latest data.</summary>
    private static (string Title, string Description) ReadHumanTask(Instance instance)
    {
        var title = string.Empty;
        var description = string.Empty;

        var latestData = instance.LatestData;
        if (latestData?.Data is null
            || latestData.Data.JsonElement.ValueKind != JsonValueKind.Object
            || !latestData.Data.JsonElement.TryGetProperty("humanTask", out var humanTask)
            || humanTask.ValueKind != JsonValueKind.Object)
        {
            return (title, description);
        }

        if (humanTask.TryGetProperty("title", out var titleProp))
            title = titleProp.GetString() ?? string.Empty;
        if (humanTask.TryGetProperty("description", out var descriptionProp))
            description = descriptionProp.GetString() ?? string.Empty;

        return (title, description);
    }

}
