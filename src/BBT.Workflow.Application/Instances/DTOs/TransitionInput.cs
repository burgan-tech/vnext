using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;

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
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = Headers,
            RouteValues = RouteValues,
            Data = new TransitionDataInfo(Data?.Key, Data?.Attributes)
            {
                Tags = Data?.Tags,
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
}