using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

public sealed class TransitionInput(
    string domain,
    string workflow,
    TransitionDataInput? data = null,
    bool sync = false)
{
    public string Domain { get; set; } = domain;

    /// <summary>
    /// Workflow key. Kept for backward compatibility (e.g. schema). For transition execution, workflow is resolved from the instance.
    /// </summary>
    public string Workflow { get; set; } = workflow;
    
    public TransitionDataInput? Data { get; set; } = data;
    public Dictionary<string, string?> Headers { get; set; } = new();
    public Dictionary<string, string?> RouteValues { get; set; } = new();
    public bool Sync { get; set; } = sync;

    /// <summary>Typed terminal-cascade context supplied by an internal terminal operation.</summary>
    public TerminationContext? Termination { get; set; }

    /// <summary>
    /// Who is triggering the transition. Defaults to <see cref="ExecutionActor.User"/> for the normal
    /// API path. The event subsystem sets <see cref="ExecutionActor.System"/> so the transition runs
    /// under the Event pipeline profile and event-actor validation.
    /// </summary>
    public ExecutionActor Actor { get; set; } = ExecutionActor.User;

    /// <summary>
    /// Extension data to evaluate and include in the sync response. Keys are extension identifiers.
    /// Evaluated only when <see cref="Sync"/> is true.
    /// </summary>
    public string[]? Extensions { get; set; }

    /// <summary>
    /// Business correlation id carried over from the originating execution (e.g. restored from a
    /// background-job payload) so an async hop continues the SAME correlation instead of minting
    /// a new one. Null on fresh client requests — a new id is minted in ToExecutionContext.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Creates a WorkflowExecutionContext from this TransitionInput for manual transition execution.
    /// </summary>
    /// <param name="instanceId">The workflow instance identifier</param>
    /// <param name="flowVersion">The workflow version</param>
    /// <param name="transitionKey">The transition key to execute</param>
    /// <returns>A new WorkflowExecutionContext instance</returns>
    public WorkflowExecutionContext ToExecutionContext(string instanceId, string flowVersion, string transitionKey)
    {
        return new WorkflowExecutionContext
        {
            Domain = Domain,
            InstanceId = instanceId,
            WorkflowKey = Workflow,
            WorkflowVersion = flowVersion,
            TransitionKey = transitionKey,
            TriggerType = TriggerType.Manual, // TransitionInput always represents manual triggers
            Mode = Sync ? ExecMode.Sync : ExecMode.Async,
            CallerMode = Sync ? ExecMode.Sync : ExecMode.Async,
            CorrelationId = CorrelationId ?? Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = Headers,
            RouteValues = RouteValues,
            Termination = Termination,
            Data = new TransitionDataInfo(Data?.Key, Data?.Attributes)
            {
                Tags = Data?.Tags,
                Stage = Data?.Stage,
            },
            IsReentry = false // Manual transitions are never re-entry
        };
    }
}

public sealed class TransitionDataInput
{
    public TransitionDataInput()
    {
        
    }

    public TransitionDataInput(JsonElement? attributes)
    {
        Attributes = attributes;
    }
    
    public string? Key { get; set; }
    public string[]? Tags { get; set; }
    public JsonElement? Attributes { get; set; }

    /// <summary>
    /// Optional stage label for the instance (max 120 characters).
    /// </summary>
    [StringLength(InstanceConstants.MaxStageLength)]
    public string? Stage { get; set; }
}
