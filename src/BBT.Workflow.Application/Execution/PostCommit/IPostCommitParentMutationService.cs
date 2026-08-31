using System.Collections.Immutable;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Reloads and mutates a parent only after acquiring its normal transition lock.
/// </summary>
public interface IPostCommitParentMutationService
{
    Task<Result<TransitionOutput>> SettleAsync(
        PostCommitParentSnapshot source,
        ContinuationSet continuations,
        CancellationToken cancellationToken);

    Task<Result<TransitionOutput>> FaultAsync(
        PostCommitParentSnapshot source,
        PostCommitFaultRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable identity and request data retained across the post-commit lock handoff.
/// Deliberately excludes the tracked parent aggregate.
/// </summary>
public sealed record PostCommitParentSnapshot
{
    public PostCommitParentSnapshot(
        string domain,
        string workflowKey,
        string workflowVersion,
        Guid instanceId,
        string transitionKey,
        ExecMode callerMode,
        string traceId,
        IReadOnlyDictionary<string, string?> headers,
        IReadOnlyDictionary<string, string?> routeValues,
        JsonElement? data,
        Definitions.Workflow workflow)
    {
        Workflow = workflow;
        Domain = domain;
        WorkflowKey = workflowKey;
        WorkflowVersion = workflowVersion;
        InstanceId = instanceId;
        TransitionKey = transitionKey;
        CallerMode = callerMode;
        TraceId = traceId;
        Headers = headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        RouteValues = routeValues.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        Data = data?.Clone();
    }

    /// <summary>
    /// The resolved workflow definition this settlement belongs to. Carried on the snapshot — unlike
    /// the parent aggregate — because a definition is immutable and cache-backed, so it crosses the
    /// post-commit lock handoff safely and saves the settlement a re-resolution.
    /// </summary>
    public Definitions.Workflow Workflow { get; }

    public string Domain { get; }
    public string WorkflowKey { get; }
    public string WorkflowVersion { get; }
    public Guid InstanceId { get; }
    public string TransitionKey { get; }
    public ExecMode CallerMode { get; }
    public string TraceId { get; }
    public IReadOnlyDictionary<string, string?> Headers { get; }
    public IReadOnlyDictionary<string, string?> RouteValues { get; }
    public JsonElement? Data { get; }

    public string LockKey => $"vnext:{Domain}:{WorkflowKey}:{InstanceId}";

    public static PostCommitParentSnapshot From(TransitionExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new PostCommitParentSnapshot(
            context.Domain,
            context.WorkflowKey,
            context.Workflow.Version,
            context.InstanceId,
            context.TransitionKey,
            context.CallerMode,
            context.TraceId,
            context.Headers,
            context.RouteValues,
            context.DataElement,
            context.Workflow);
    }
}
