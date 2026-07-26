using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Dapr Conversation Task Definition.
/// Invokes an LLM/AI provider through the Dapr Conversation building block, giving workflows a
/// provider-agnostic way to run prompts (OpenAI, Anthropic, AWS Bedrock, etc.) over the same
/// Dapr sidecar used by the other Dapr task types.
/// </summary>
public sealed class DaprConversationTask : WorkflowTask
{
    private DaprConversationTask()
    {

    }

    [JsonConstructor]
    private DaprConversationTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.DaprConversation).ToString();
    }

    /// <summary>
    /// The Dapr conversation component name (the configured LLM provider), e.g. "openai".
    /// </summary>
    public string ComponentName { get; private set; } = string.Empty;

    /// <summary>
    /// Conversation inputs as a JSON array of messages, each shaped as
    /// <c>{ "role": "user|system|assistant|developer|tool", "content": "...", "scrubPII": bool?, "name": "..." }</c>.
    /// </summary>
    public JsonElement Inputs { get; private set; }

    /// <summary>
    /// Provider-specific parameters (e.g. model, maxTokens) forwarded to the component as string values.
    /// </summary>
    public JsonElement? Parameters { get; private set; }

    /// <summary>
    /// Dapr component metadata forwarded with the request as string values.
    /// </summary>
    public JsonElement? Metadata { get; private set; }

    /// <summary>
    /// Optional context identifier used to continue a stateful conversation.
    /// </summary>
    public string? ContextId { get; private set; }

    /// <summary>
    /// Optional sampling temperature.
    /// </summary>
    public double? Temperature { get; private set; }

    /// <summary>
    /// When true, requests the provider scrub PII from prompts and responses.
    /// </summary>
    public bool? ScrubPII { get; private set; }

    /// <summary>
    /// Timeout seconds.
    /// </summary>
    public int TimeoutSeconds { get; private set; } = 30;

    public void SetComponentName(string componentName) => ComponentName = componentName;
    public void SetContextId(string? contextId) => ContextId = contextId;
    public void SetTimeoutSeconds(int? timeoutSeconds) => TimeoutSeconds = timeoutSeconds ?? 30;
    public void SetScrubPII(bool? scrubPII) => ScrubPII = scrubPII;
    public void SetTemperature(double? temperature) => Temperature = temperature;

    public void SetInputs(dynamic inputs)
    {
        Inputs = JsonSerializer.SerializeToElement(inputs);
    }

    public void SetParameters(Dictionary<string, string?> parameters)
    {
        Parameters = JsonSerializer.SerializeToElement(parameters);
    }
    
    public void SetMetadata(dynamic metadata)
    {
        Metadata = JsonSerializer.SerializeToElement(metadata);
    }
    
    public void SetMetadata(Dictionary<string, string?> metadata)
    {
        Metadata = JsonSerializer.SerializeToElement(metadata);
    }

    /// <summary>
    /// Internal property setters for object pooling.
    /// </summary>
    internal void SetComponentNameInternal(string componentName) => ComponentName = componentName;
    internal void SetInputsInternal(JsonElement inputs) => Inputs = inputs;
    internal void SetParametersInternal(JsonElement? parameters) => Parameters = parameters;
    internal void SetMetadataInternal(JsonElement? metadata) => Metadata = metadata;
    internal void SetContextIdInternal(string? contextId) => ContextId = contextId;
    internal void SetTemperatureInternal(double? temperature) => Temperature = temperature;
    internal void SetScrubPiiInternal(bool? scrubPii) => ScrubPII = scrubPii;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("componentName", out var componentName))
            ComponentName = componentName.GetString() ?? throw new ArgumentNullException(nameof(componentName));

        if (config.TryGetProperty("inputs", out var inputs))
            Inputs = inputs.Clone();

        if (config.TryGetProperty("parameters", out var parameters))
            Parameters = parameters.Clone();

        if (config.TryGetProperty("metadata", out var metadata))
            Metadata = metadata.Clone();

        if (config.TryGetProperty("contextId", out var contextId))
            ContextId = contextId.GetString();

        if (config.TryGetProperty("temperature", out var temperature) &&
            temperature.ValueKind is JsonValueKind.Number)
            Temperature = temperature.GetDouble();

        if (config.TryGetProperty("scrubPII", out var scrubPii) &&
            scrubPii.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ScrubPII = scrubPii.GetBoolean();

        if (config.TryGetProperty("timeoutSeconds", out var timeout))
            TimeoutSeconds = timeout.GetInt32();
    }

    public static DaprConversationTask Create(
        JsonElement config)
    {
        return new DaprConversationTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current DaprConversationTask instance.
    /// </summary>
    /// <returns>A new DaprConversationTask instance with identical configuration.</returns>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current DaprConversationTask instance.
    /// </summary>
    /// <returns>A new DaprConversationTask instance with identical configuration.</returns>
    public DaprConversationTask CloneTyped()
    {
        var cloned = new DaprConversationTask();
        CopyBaseTo(cloned);

        cloned.ComponentName = ComponentName;
        cloned.Inputs = Inputs; // JsonElement is a struct, so this is safe
        cloned.Parameters = Parameters;
        cloned.Metadata = Metadata;
        cloned.ContextId = ContextId;
        cloned.Temperature = Temperature;
        cloned.ScrubPII = ScrubPII;
        cloned.TimeoutSeconds = TimeoutSeconds;

        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently.
    /// </summary>
    /// <param name="source">Source task to copy from.</param>
    public void CopyFromInternal(DaprConversationTask source)
    {
        source.CopyBaseToInternal(this);
        SetComponentNameInternal(source.ComponentName);
        SetInputsInternal(source.Inputs);
        SetParametersInternal(source.Parameters);
        SetMetadataInternal(source.Metadata);
        SetContextIdInternal(source.ContextId);
        SetTemperatureInternal(source.Temperature);
        SetScrubPiiInternal(source.ScrubPII);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling.
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        ComponentName = string.Empty;
        Inputs = default;
        Parameters = null;
        Metadata = null;
        ContextId = null;
        Temperature = null;
        ScrubPII = null;
        TimeoutSeconds = 30;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only.
    /// </summary>
    public static DaprConversationTask CreateEmpty()
    {
        return new DaprConversationTask();
    }
}
