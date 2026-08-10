using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution;

/// <summary>
/// Input data for workflow execution operations.
/// Contains all necessary information to execute a transition.
/// </summary>
public sealed class WorkflowExecutionContext
{
    /// <summary>Gets or sets the domain/tenant identifier.</summary>
    [Enrich(Name = "vnext.domain")]
    public string Domain { get; set; } = default!;
    
    /// <summary>Gets or sets the workflow instance identifier.</summary>
    [Enrich(Name = "vnext.instance.id")]
    public string InstanceId { get; set; }
    
    /// <summary>Gets or sets the workflow key.</summary>
    [Enrich(Name = "vnext.flow.key")]
    public string WorkflowKey { get; set; } = default!;
    
    /// <summary>Gets or sets the workflow version (optional, uses latest if not specified).</summary>
    [Enrich(Name = "vnext.flow.version")]
    public string? WorkflowVersion { get; set; }
    
    /// <summary>Gets or sets the transition key to execute.</summary>
    [Enrich(Name = "vnext.transition.key")]
    public string TransitionKey { get; set; } = default!;
    
    /// <summary>Gets or sets the trigger type for this execution.</summary>
    public TriggerType TriggerType { get; set; }
    
    /// <summary>Gets or sets the execution mode (sync/async).</summary>
    public ExecMode Mode { get; set; } = ExecMode.Sync;

    /// <summary>Gets or sets the original caller's execution mode intent. Used for subflow propagation; defaults to Mode.</summary>
    public ExecMode CallerMode { get; set; } = ExecMode.Sync;
    
    /// <summary> Get or sets the execution actor (default: User) </summary>
    public ExecutionActor Actor { get; set; } = ExecutionActor.User;
    
    /// <summary>Gets or sets the correlation identifier.</summary>
    public string? CorrelationId { get; set; }
    
    /// <summary>Gets or sets the causation identifier.</summary>
    public string? CausationId { get; set; }
    
    /// <summary>Gets or sets the timestamp when this execution was requested.</summary>
    public DateTimeOffset? RequestedAt { get; set; }
    
    /// <summary>Gets or sets the request headers.</summary>
    public Dictionary<string, string?> Headers { get; set; } = new();
    
    /// <summary>Gets or sets the execution context information for re-entry scenarios.</summary>
    public ExecutionInfo? Execution { get; set; }
    
    /// <summary>Gets or sets whether this is a re-entry execution.</summary>
    public bool IsReentry { get; set; }

    /// <summary>
    /// Gets or sets whether auto-chain continuations should be enqueued as separate background
    /// jobs (transition-per-job) rather than executed in-process. Set by the job handler when
    /// the TransitionPerJob feature is enabled. Propagated to <see cref="TransitionExecutionContext"/>.
    /// </summary>
    public bool EnqueueContinuations { get; set; }

    /// <summary>
    /// Gets or sets the incoming auto-chain ownership token carried by a continuation (S6).
    /// Null for a fresh request (a new token is minted at SetBusy); non-null when this execution
    /// is the chain's own continuation, which the chain-token gate admits past a Busy instance.
    /// </summary>
    public Guid? ChainToken { get; set; }

    /// <summary>
    /// Revision observed by the caller or recorded by async admission. It is an informational
    /// preflight snapshot unless <see cref="EnforceExpectedRevision"/> is enabled by the durable
    /// consumer.
    /// </summary>
    public long? ExpectedRevision { get; set; }

    /// <summary>
    /// Requires <see cref="ExpectedRevision"/> to match the authoritative snapshot loaded after
    /// the execution lock is acquired. Set only by durable job consumers; HTTP admission
    /// snapshots are refreshed under the lock instead of being rejected as stale.
    /// </summary>
    public bool EnforceExpectedRevision { get; set; }

    /// <summary>
    /// True when the transition request body has already passed its input schema at the HTTP
    /// admission boundary. Authoritative execution still re-checks state policy, but skips the
    /// same immutable schema/cache work.
    /// </summary>
    public bool TransitionSchemaValidated { get; set; }

    /// <summary>Gets or sets typed terminal-cascade context for this execution.</summary>
    public TerminationContext? Termination { get; set; }

    /// <summary>Gets or sets whether this transition was requested by an error boundary (e.g. Rollback/Notify). When true, state policy checks are bypassed so the transition can run from any state.</summary>
    public bool IsErrorBoundaryTransition { get; set; }
    
    /// <summary>Gets or sets the transition data payload.</summary>
    public TransitionDataInfo? Data { get; set; }
    
    /// <summary>Gets or sets the route values from the HTTP request.</summary>
    public Dictionary<string, string?> RouteValues { get; set; } = new();
    
    /// <summary>Gets or sets retry information for retry operations.</summary>
    public RetryInfo? Retry { get; set; }
}

/// <summary>
/// Contains information about a retry operation.
/// </summary>
public sealed class RetryInfo
{
    /// <summary>Gets or sets the ID of the transition being retried.</summary>
    public Guid TransitionId { get; set; }
}

public sealed class TransitionDataInfo 
{
    public TransitionDataInfo()
    {
        
    }
    
    public TransitionDataInfo(JsonElement? attributes)
    {
        Attributes = attributes;
    }

    public TransitionDataInfo(string? key, JsonElement? attributes) : this(attributes)
    {
        Key = key;
    }
    
    public string? Key { get; set; }
    public JsonElement? Attributes  { get; set; }
    public string[]? Tags { get; set; }
    public string? Stage { get; set; }
}
