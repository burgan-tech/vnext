using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Workflow.Definitions;
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
    [Enrich(Name = "vnext.instanceid")]
    public string InstanceId { get; set; }
    
    /// <summary>Gets or sets the workflow key.</summary>
    [Enrich(Name = "vnext.flow.key")]
    public string WorkflowKey { get; set; } = default!;
    
    /// <summary>Gets or sets the workflow version (optional, uses latest if not specified).</summary>
    [Enrich(Name = "vnext.flow.version")]
    public string? WorkflowVersion { get; set; }
    
    /// <summary>Gets or sets the transition key to execute.</summary>
    [Enrich(Name = "vnext.flow.transition")]
    public string TransitionKey { get; set; } = default!;
    
    /// <summary>Gets or sets the trigger type for this execution.</summary>
    public TriggerType TriggerType { get; set; }
    
    /// <summary>Gets or sets the execution mode (sync/async).</summary>
    public ExecMode Mode { get; set; } = ExecMode.Sync;
    
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
    
    /// <summary>Gets or sets the transition data payload.</summary>
    public TransitionDataInfo? Data { get; set; }
    
    /// <summary>Gets or sets the route values from the HTTP request.</summary>
    public Dictionary<string, string?> RouteValues { get; set; } = new();
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
}