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
    /// Gets or sets whether the instance was already reserved (flipped to Busy) for this
    /// execution before the pipeline runs — set by the transition job handler for async-accept
    /// re-entries and chain continuation jobs. Pre-reserved executions skip the Busy admission
    /// check and the reserve.
    /// </summary>
    public bool IsPreReserved { get; set; }

    /// <summary>
    /// Gets or sets whether the accept that produced this execution reserved the instance's whole
    /// active SubFlow chain down to the leaf. Read by <c>ForwardToActiveSubflowStep</c> to stamp
    /// the claim onto the forward so the leaf admits it as an owner re-entry rather than 409-ing
    /// on the Busy the accept pre-set. Distinct from <see cref="IsPreReserved"/>, which every job
    /// re-entry sets: only a genuine chain reserve may be claimed.
    /// </summary>
    public bool SubflowChainReserved { get; set; }

    /// <summary>
    /// Gets or sets whether this execution owns the instance's Busy lifecycle. Set by the
    /// pipeline admission (reserve/takeover/owner re-entry ⇒ true; subflow forward ⇒ false;
    /// updateData ⇒ opportunistic). Only status owners may resolve/settle the instance status —
    /// a non-owning updateData running beside an in-flight chain must never flip the owner's
    /// Busy. Propagated across inline continuation hops.
    /// </summary>
    public bool OwnsStatus { get; set; }

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
