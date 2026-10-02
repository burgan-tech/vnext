using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Correlation;

/// <summary>
/// Expands one hop of a correlation tree — every requested instance of one flow, with their
/// descendants resolved recursively and sibling hops expanded concurrently.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a depth-first <c>foreach … await</c> that issued two queries PER NODE and never left
/// the local database. Three things changed and each matters on its own:
/// </para>
/// <list type="number">
/// <item>
/// <b>The level is the unit, not the node.</b> One <c>GetByParentsAsync</c> answers for every
/// instance in the hop, and one instance read answers for every requested id — so a level of width
/// W costs 2 queries instead of 2W.
/// </item>
/// <item>
/// <b>A hop crosses a domain boundary once per BRANCH.</b> The far side re-enters this same
/// resolver and recurses on its own, so a six-level subtree in another domain costs one remote
/// call, not six.
/// </item>
/// <item>
/// <b>Sibling hops run concurrently</b>, bounded twice over — see the fan-out comment below. Depth
/// is still serial, and inherently so: a level cannot be grouped until its parent has answered.
/// </item>
/// </list>
/// <para>
/// Nothing here authorizes. The function this serves carries no gate (consistent with the other
/// instance read surfaces, which answer <c>queryRoles</c> through
/// <c>authorize?queryRoles=true</c> rather than enforcing it), and the internal endpoint in front
/// of it is protected by network isolation only.
/// </para>
/// </remarks>
public sealed class InstanceCorrelationResolver(
    IInstanceCorrelationRepository correlationRepository,
    IInstanceRepository instanceRepository,
    ICurrentSchema currentSchema,
    IUrlTemplateBuilder urlTemplateBuilder,
    IServiceScopeFactory scopeFactory,
    IInstanceCorrelationGateway gateway,
    CorrelationHopLimiter hopLimiter,
    IOptions<InstanceCorrelationOptions> options,
    ILogger<InstanceCorrelationResolver> logger) : IInstanceCorrelationResolver
{
    /// <summary>The walk stopped because <see cref="InstanceCorrelationOptions.MaxDescentDepth"/> ran out.</summary>
    internal const string DepthExceeded = "depth-exceeded";

    /// <summary>A hop failed — most often an unreachable partner domain.</summary>
    internal const string HopFailed = "hop-failed";

    /// <summary>
    /// The partner domain answered 404 for the hop endpoint itself — it runs a runtime that
    /// predates the batch route, so it can never serve this branch.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="HopFailed"/> on purpose. Domains upgrade independently, so a
    /// partner on an older runtime is an expected state, not an anomaly — and it is PERMANENT
    /// until that domain is upgraded, where a transient failure is worth retrying. Collapsing the
    /// two would make a version-skew problem look like a flaky network and send clients into a
    /// retry loop against a domain that will never answer.
    /// </remarks>
    internal const string HopUnsupported = "hop-unsupported";

    /// <summary>The instance row could not be read in the domain that should own it.</summary>
    internal const string InstanceMissing = "instance-missing";

    private readonly InstanceCorrelationOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<CorrelationBatchResult>>> ResolveAsync(
        string domain,
        string flow,
        CorrelationBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InstanceIds.Count == 0)
        {
            return Result<IReadOnlyList<CorrelationBatchResult>>.Ok([]);
        }

        // Distinct up front: the same instance can legitimately be reached twice in one level only
        // through a malformed graph, but the batch read would then duplicate its rows and the
        // result would carry the node twice.
        var ids = request.InstanceIds.Distinct().ToArray();

        // CLAMPED to this runtime's own bound, never taken on trust. RemainingDepth arrives from
        // the caller on the internal batch endpoint, which carries no authorization — so without
        // this a caller could hand in any depth it liked and walk straight past the one thing
        // standing between a cyclic correlation graph and an infinite walk. Nothing can cycle today
        // (correlations are written only at spawn time), but the backstop has to hold the moment a
        // post-hoc correlation write lands, and a guard that is only correct until a future feature
        // ships is not a guard. Mirrors the MaxInstanceIds check the endpoint already applies to the
        // other half of this request. A no-op for every internal caller: the recursion only ever
        // passes RemainingDepth - 1, and the public read starts at MaxDescentDepth.
        var remainingDepth = Math.Min(request.RemainingDepth, _options.MaxDescentDepth);

        // Depth is checked BEFORE any query. Running the reads and then discarding them would pay
        // the whole cost of the level to report that it was out of budget.
        if (remainingDepth <= 0)
        {
            logger.CorrelationWalkDepthExceeded(domain, flow, ids.Length, _options.MaxDescentDepth);
            return Result<IReadOnlyList<CorrelationBatchResult>>.Ok(
                [.. ids.Select(id => new CorrelationBatchResult
                {
                    InstanceId = id,
                    Resolved = false,
                    UnresolvedReason = DepthExceeded
                })]);
        }

        // ── One read for the instances themselves, one for the whole level's correlations ───────
        //
        // Both run in this flow's schema and share this hop's unit of work, which is correct and
        // deliberate: sequential reads on one connection are safe, and it keeps open connections
        // bounded by the fan-out WIDTH rather than width x depth.
        List<Instance> selves;
        List<InstanceCorrelation> correlations;
        using (currentSchema.Change(flow))
        {
            selves = await instanceRepository.GetForCorrelationWalkAsync(ids, cancellationToken);
            correlations = await correlationRepository.GetByParentsAsync(ids, cancellationToken);
        }

        var selfById = selves.ToDictionary(i => i.Id);
        var childrenByParent = correlations
            .GroupBy(c => c.ParentInstanceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build every child node of every requested instance, then expand them all together: the
        // fan-out is over the WHOLE level, so two parents whose children live in the same
        // (domain, flow) share one hop instead of taking one each.
        var nodesByParent = new Dictionary<Guid, List<InstanceCorrelationNode>>();
        var allChildNodes = new List<(InstanceCorrelationNode Node, InstanceCorrelation Correlation)>();

        foreach (var id in ids)
        {
            if (!childrenByParent.TryGetValue(id, out var links))
            {
                nodesByParent[id] = [];
                continue;
            }

            var nodes = new List<InstanceCorrelationNode>(links.Count);
            foreach (var correlation in links)
            {
                var node = BuildNodeFromLink(correlation);
                nodes.Add(node);
                allChildNodes.Add((node, correlation));
            }

            nodesByParent[id] = nodes;
        }

        if (allChildNodes.Count > 0)
        {
            await ExpandAsync(allChildNodes, remainingDepth - 1, cancellationToken);
        }

        return Result<IReadOnlyList<CorrelationBatchResult>>.Ok(
            [.. ids.Select(id =>
            {
                var found = selfById.TryGetValue(id, out var self);
                return new CorrelationBatchResult
                {
                    InstanceId = id,
                    // A missing row is reported, never silently rendered as a childless leaf: under
                    // READ COMMITTED the row can disappear between the parent's correlation read and
                    // this one, and a caller must be able to tell that from "no children".
                    Resolved = found,
                    UnresolvedReason = found ? null : InstanceMissing,
                    Key = self?.Key,
                    OwnState = self?.CurrentState,
                    Status = self?.Status,
                    FlowVersion = self?.FlowVersion,
                    Children = nodesByParent.TryGetValue(id, out var children) ? children : []
                };
            })]);
    }

    /// <summary>
    /// Expands a whole level: groups it into hops, runs them concurrently, and writes each hop's
    /// answer back onto the nodes it belonged to.
    /// </summary>
    private async Task ExpandAsync(
        List<(InstanceCorrelationNode Node, InstanceCorrelation Correlation)> level,
        int remainingDepth,
        CancellationToken cancellationToken)
    {
        if (remainingDepth <= 0)
        {
            // Mark rather than recurse. The nodes themselves are real and stay in the tree; only
            // their descendants are unknown, and the caller is told which.
            foreach (var (node, _) in level)
            {
                node.Resolved = false;
                node.UnresolvedReason = DepthExceeded;
            }

            // Logged HERE, not only on the entry guard. This is the branch a real walk actually
            // takes — the guard in ResolveAsync fires only when a CALLER hands in an exhausted
            // budget, which this resolver never does because it stops one level earlier. Measured
            // on a six-level chain with the bound set to three: the nodes were marked correctly and
            // the runtime logged nothing at all, so a truncated tree was invisible to operations.
            var first = level[0].Correlation;
            logger.CorrelationWalkDepthExceeded(
                first.SubFlowDomain, first.SubFlowName, level.Count, _options.MaxDescentDepth);

            return;
        }

        // One hop per (domain, flow, version). Version participates because it is what the request
        // carries forward, and two versions of one flow can live in different schemas.
        var hops = level
            .GroupBy(entry => (
                Domain: entry.Correlation.SubFlowDomain,
                Flow: entry.Correlation.SubFlowName,
                Version: entry.Correlation.SubFlowVersion))
            .ToList();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.FanoutParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(hops, parallelOptions, async (hop, ct) =>
        {
            var members = hop.ToList();
            var key = hop.Key;

            // Acquired BEFORE the unit of work, so a branch waiting for a slot is not waiting while
            // holding a pooled connection. This is the only ceiling that spans requests; without it
            // one request's width multiplies by however many callers happen to coincide, which is
            // precisely how the human-task fan-out exhausted the connection pool.
            using var slot = await hopLimiter.AcquireAsync(ct);

            Result<IReadOnlyList<CorrelationBatchResult>> hopResult;
            try
            {
                hopResult = await scopeFactory.ExecuteInIsolatedUnitOfWorkAsync(
                    (_, innerCt) => gateway.ResolveAsync(
                        key.Domain,
                        key.Flow,
                        new CorrelationBatchRequest
                        {
                            InstanceIds = [.. members.Select(m => m.Correlation.SubFlowInstanceId)],
                            RemainingDepth = remainingDepth,
                            FlowVersion = key.Version
                        },
                        innerCt),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transport fault must not take the whole tree down with it — one unreachable
                // partner domain would blank a view the caller mostly can see.
                MarkHop(members, HopFailed);
                logger.CorrelationHopFailed(key.Domain, key.Flow, members.Count, ex.Message);
                return;
            }

            if (!hopResult.IsSuccess || hopResult.Value is null)
            {
                // A 404 is the hop ENDPOINT being absent, not an instance being absent: this route
                // answers per-id results in the body and never 404s for a missing instance. So it
                // means the far side predates the batch endpoint.
                var reason = hopResult.Error.Prefix == ErrorCodes.Prefixes.NotFound
                    ? HopUnsupported
                    : HopFailed;
                MarkHop(members, reason);
                logger.CorrelationHopFailed(
                    key.Domain, key.Flow, members.Count, hopResult.Error.Message ?? "unknown");
                return;
            }

            var answerById = hopResult.Value.ToDictionary(r => r.InstanceId);
            foreach (var (node, correlation) in members)
            {
                if (!answerById.TryGetValue(correlation.SubFlowInstanceId, out var answer))
                {
                    node.Resolved = false;
                    node.UnresolvedReason = HopFailed;
                    continue;
                }

                ApplyAnswer(node, answer);
            }
        });
    }

    /// <summary>
    /// Builds a child node from the correlation row alone — everything the LINK knows. What the
    /// instance itself knows is merged in later by <see cref="ApplyAnswer"/>, because only the
    /// domain owning that instance can read its row.
    /// </summary>
    private InstanceCorrelationNode BuildNodeFromLink(InstanceCorrelation correlation) => new()
    {
        Id = correlation.SubFlowInstanceId,
        Flow = correlation.SubFlowName,
        Domain = correlation.SubFlowDomain,
        FlowVersion = correlation.SubFlowVersion,
        CurrentState = correlation.SubFlowCurrentState,
        // Status from the link is a FALLBACK only, replaced by the live one when the owning domain
        // answers. Kept so a node whose hop failed still carries something defensible.
        Status = correlation.IsCompleted ? InstanceStatus.Completed : InstanceStatus.Active,
        SubFlowType = correlation.SubFlowType,
        IsCompleted = correlation.IsCompleted,
        CompletedAt = correlation.CompletedAt,
        ParentState = correlation.ParentState,
        CorrelationId = correlation.Id,
        CreatedAt = correlation.CreatedAt,
        TerminalOutcome = correlation.TerminalOutcome,
        StateChangedAt = correlation.SubFlowStateChangedAt,
        Href = urlTemplateBuilder.BuildInstanceUrl(
            correlation.SubFlowDomain,
            correlation.SubFlowName,
            correlation.SubFlowInstanceId.ToString())
    };

    /// <summary>Merges a hop's answer for one instance into the node its parent built.</summary>
    private static void ApplyAnswer(InstanceCorrelationNode node, CorrelationBatchResult answer)
    {
        node.Key = answer.Key;
        node.OwnState = answer.OwnState;

        // The link's bubbled state reports the DEEPEST active descendant and is the right value for
        // CurrentState; it is only absent on a correlation nothing has bubbled into yet, and the
        // node's own state is the honest stand-in there.
        node.CurrentState ??= answer.OwnState;

        if (answer.Status is not null)
        {
            node.Status = answer.Status;
        }

        if (!string.IsNullOrEmpty(answer.FlowVersion))
        {
            node.FlowVersion = answer.FlowVersion;
        }

        node.Children = [.. answer.Children];
        node.Resolved = answer.Resolved;
        node.UnresolvedReason = answer.UnresolvedReason;
    }

    private static void MarkHop(
        List<(InstanceCorrelationNode Node, InstanceCorrelation Correlation)> members,
        string reason)
    {
        foreach (var (node, _) in members)
        {
            node.Resolved = false;
            node.UnresolvedReason = reason;
        }
    }
}
