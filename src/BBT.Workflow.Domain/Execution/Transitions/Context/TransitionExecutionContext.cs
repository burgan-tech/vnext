using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Aether.Aspects;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution;

/// <summary>
/// Contains all necessary context data for transition execution.
/// This is a minimal, service-free context that carries only essential data and state.
/// Services are injected into pipeline steps and handlers, not stored in the context.
/// </summary>
public sealed class TransitionExecutionContext
{
    // Identity (immutable)
    /// <summary>Gets the domain/tenant identifier.</summary>
    [Enrich(Name = "vnext.domain")]
    public string Domain { get; init; } = default!;

    /// <summary>Gets the workflow instance identifier.</summary>
    [Enrich(Name = "vnext.instanceid")]
    public Guid InstanceId { get; init; }

    /// <summary>Gets the workflow key.</summary>
    [Enrich(Name = "vnext.flow.key")]
    public string WorkflowKey { get; init; } = default!;

    /// <summary>Gets the transition key being executed.</summary>
    [Enrich(Name = "vnext.flow.transition")]
    public string TransitionKey { get; init; } = default!;

    /// <summary>Gets or sets instance key (Optional).</summary>
    public string? InstanceKey { get; set; }
    
    /// <summary>Gets or sets instance tags (Optional).</summary>
    public string[]? Tags { get; set; }

    /// <summary>Gets the trigger type that initiated this transition.</summary>
    public TriggerType Trigger { get; init; }

    /// <summary> Get or sets the execution actor (default: User) </summary>
    public ExecutionActor Actor { get; set; } = ExecutionActor.User;

    /// <summary>Gets the correlation identifier for tracking related operations.</summary>
    public string CorrelationId { get; init; } = default!;

    /// <summary>Gets the causation identifier linking this transition to its cause.</summary>
    public string? CausationId { get; init; }

    /// <summary>Gets the execution chain identifier for re-entry tracking.</summary>
    public string ExecutionChainId { get; init; } = default!;

    /// <summary>Gets the depth in the execution chain (for automatic transitions).</summary>
    public int ChainDepth { get; init; }

    /// <summary>Gets the timestamp when this transition was requested.</summary>
    public DateTimeOffset RequestedAt { get; init; }

    // Definitions (rehydrated)
    /// <summary>Gets the workflow definition.</summary>
    public Definitions.Workflow Workflow { get; init; } = default!;

    /// <summary>Gets or sets the current workflow state.</summary>
    public State Current { get; set; } = default!;

    /// <summary>Gets or sets the target workflow state (set during execution).</summary>
    public State? Target { get; set; }

    /// <summary>Gets the transition definition being executed.</summary>
    public Transition? Transition { get; init; } = default!;

    // Instance snapshot
    /// <summary>Gets or sets the workflow instance aggregate.</summary>
    public Instance Instance { get; set; } = default!;

    /// <summary>Gets or sets the concurrency token for optimistic locking.</summary>
    public string ConcurrencyToken { get; set; } = default!;

    /// <summary>Gets or sets the instance data payload.</summary>
    public object? Data { get; set; }

    // Execution flags
    /// <summary>Gets or sets whether to skip immediate execution (for scheduled transitions).</summary>
    public bool SkipImmediateExecution { get; set; }

    /// <summary>Gets whether this is a re-entry execution (automatic/scheduled).</summary>
    public bool IsReentry { get; init; }

    /// <summary>Gets whether this transition was requested by an error boundary (e.g. Rollback/Notify). When true, state policy checks are bypassed so the transition can run from any state.</summary>
    public bool IsErrorBoundaryTransition { get; init; }

    // Telemetry & Headers & Temporary storage
    /// <summary>Gets the distributed tracing trace identifier.</summary>
    public string TraceId { get; init; } = default!;

    /// <summary>Gets the distributed tracing span identifier.</summary>
    public string SpanId { get; init; } = default!;

    /// <summary>Gets the request headers.</summary>
    public IReadOnlyDictionary<string, string?> Headers { get; init; } = new Dictionary<string, string?>();

    /// <summary>Gets the request route values.</summary>
    public IReadOnlyDictionary<string, string?> RouteValues { get; init; } = new Dictionary<string, string?>();

    /// <summary>Gets a temporary storage bag for pipeline steps to share data.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public IDictionary<string, object?> Cache { get; } = new Dictionary<string, object?>();
    public void ClearCacheForFinalize() => Cache.Clear();

    // Typed instructions
    public PipelineDirectives Directives { get; } = new();

    public ClientResponse? ClientResponse { get; set; }

    /// <summary>
    /// Gets the distributed lock key for this instance.
    /// Used by TransitionPipeline to acquire/release instance-level locks.
    /// </summary>
    public string LockKey => $"vnext:{Domain}:{WorkflowKey}:{InstanceId}";

    public JsonElement? DataElement => Data switch
    {
        JsonElement element => element,
        string jsonString => JsonSerializer.Deserialize<JsonElement>(jsonString),
        null => null,
        _ => JsonSerializer.SerializeToElement(Data)
    };

    /// <summary>
    /// Gets or builds a ScriptContext using the provided factory function.
    /// The ScriptContext is cached in Cache to avoid recreating it multiple times.
    /// </summary>
    /// <param name="factory">Async factory function to create a new ScriptContext if not cached.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>The cached or newly created ScriptContext.</returns>
    public async Task<ScriptContext> GetOrBuildScriptContextAsync(
        Func<CancellationToken, Task<ScriptContext>> factory,
        CancellationToken cancellationToken = default)
    {
        if (Cache.TryGetValue("ScriptContext", out var cached) && cached is ScriptContext scriptContext)
            return scriptContext;

        var created = await factory(cancellationToken);
        Cache["ScriptContext"] = created;
        return created;
    }

    /// <summary>
    /// Applies changes made within the provided <see cref="ScriptContext"/> back to the live transition context.
    /// </summary>
    /// <param name="scriptContext">The script context containing potential instance updates.</param>
    public void ApplyScriptContextChanges(ScriptContext scriptContext)
    {
        ArgumentNullException.ThrowIfNull(scriptContext);

        var scriptInstance = scriptContext.Instance;
        if (scriptInstance == null || Instance == null)
        {
            return;
        }

        var applied = false;
        var existingIds = new HashSet<Guid>(Instance.DataList.Select(data => data.Id));

        foreach (var data in scriptInstance.DataList)
        {
            if (!existingIds.Add(data.Id))
            {
                continue;
            }

            Instance.AddDataWithVersion(
                data.Id,
                new JsonData(data.Data.Json),
                data.Version);

            applied = true;
        }

        if (applied)
        {
            Data = Instance.Data;
        }
    }
}