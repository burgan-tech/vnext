using System.Text.Json;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Marker interface for post-commit jobs.
/// Post-commit jobs are executed after the distributed lock is released,
/// ensuring that side effects like remote calls don't block the lock.
/// </summary>
public interface IPostCommitJob
{
}

/// <summary>
/// Interface for post-commit jobs that support idempotency.
/// Jobs implementing this interface will be tracked by the idempotency store
/// to prevent duplicate execution on retries.
/// </summary>
public interface IIdempotentPostCommitJob : IPostCommitJob
{
    /// <summary>
    /// Gets the unique key used to track idempotency for this job.
    /// Should be deterministic and unique per logical operation.
    /// </summary>
    string IdempotencyKey { get; }
}

/// <summary>
/// Post-commit job for starting a subflow.
/// Contains only the data needed to start the subflow after commit.
/// Implements idempotency to prevent duplicate subflow starts on retries.
/// </summary>
/// <param name="CorrelationId">The correlation ID linking parent and subflow instances.</param>
/// <param name="TargetStateKey">The key of the target state containing subflow configuration.</param>
public sealed record StartSubflowJob(
    Guid CorrelationId,
    string TargetStateKey) : IIdempotentPostCommitJob
{
    /// <inheritdoc />
    public string IdempotencyKey => $"subflow:{CorrelationId}";
}

/// <summary>
/// Post-commit job for forwarding a transition to an active subflow.
/// Contains the data needed to forward the transition after lock release.
/// </summary>
/// <param name="SubflowInstanceId">The instance ID of the active subflow to forward to.</param>
/// <param name="ParentInstanceId">The parent instance ID for trace/log correlation and remote request header.</param>
/// <param name="TransitionKey">The transition key being forwarded.</param>
/// <param name="SubflowDomain">The domain of the subflow.</param>
/// <param name="SubflowName">The workflow name of the subflow.</param>
/// <param name="SubflowVersion">The version of the subflow workflow.</param>
/// <param name="InstanceKey">The key of the parent instance.</param>
/// <param name="Stage">The stage from the transition.</param>
/// <param name="Tags">The tags from the transition.</param>
/// <param name="DataElement">The data element to forward.</param>
/// <param name="Headers">The headers to forward.</param>
/// <param name="RouteValues">The route values to forward.</param>
public sealed record ForwardToSubflowJob(
    Guid SubflowInstanceId,
    Guid ParentInstanceId,
    string TransitionKey,
    string SubflowDomain,
    string SubflowName,
    string? SubflowVersion,
    string? InstanceKey,
    string? Stage,
    string[]? Tags,
    JsonElement? DataElement,
    Dictionary<string, string?> Headers,
    Dictionary<string, string?> RouteValues) : IPostCommitJob
{
}

