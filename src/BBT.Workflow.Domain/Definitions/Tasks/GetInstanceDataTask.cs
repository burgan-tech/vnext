using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Get Instance Data Task Definition - Retrieves instance data from a workflow instance
/// </summary>
public sealed class GetInstanceDataTask : WorkflowTask
{
    private GetInstanceDataTask()
    {
    }

    [JsonConstructor]
    private GetInstanceDataTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.GetInstanceData).ToString();
    }

    /// <summary>
    /// Domain of the target workflow
    /// </summary>
    public string TriggerDomain { get; private set; } = string.Empty;

    /// <summary>
    /// Flow name of the target workflow
    /// </summary>
    public string TriggerFlow { get; private set; } = string.Empty;

    /// <summary>
    /// Flow key of the target workflow (optional)
    /// </summary>
    public string? TriggerKey { get; private set; }

    /// <summary>
    /// InstanceId of the target workflow (optional)
    /// </summary>
    public Guid? TriggerInstanceId { get; private set; }

    /// <summary>
    /// Extensions to request for data enrichment (optional)
    /// </summary>
    public string[]? Extensions { get; private set; }

    /// <summary>
    /// Whether to use Dapr service invocation instead of direct HTTP
    /// </summary>
    public bool UseDapr { get; private set; } = false;

    public string? Identifier => TriggerInstanceId.HasValue ? TriggerInstanceId.Value.ToString() : TriggerKey;

    public void SetInstance(string? instanceId)
    {
        if (!instanceId.IsNullOrWhiteSpace())
        {
            TriggerInstanceId = Guid.TryParse(instanceId,  out var guid) ? guid : null;    
        }
        else
        {
            TriggerInstanceId = null;
        }
    }

    public void SetKey(string? key)
    {
        TriggerKey = key;
    }

    public void SetDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain, nameof(domain));
        TriggerDomain = domain;
    }

    public void SetFlow(string flow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow, nameof(flow));
        TriggerFlow = flow;
    }

    public void SetExtensions(string[] extensions)
    {
        Extensions = extensions;
    }

    public void SetUseDapr(bool useDapr)
    {
        UseDapr = useDapr;
    }

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetTriggerDomainInternal(string triggerDomain) => TriggerDomain = triggerDomain;
    internal void SetTriggerFlowInternal(string triggerFlow) => TriggerFlow = triggerFlow;
    internal void SetTriggerKeyInternal(string? triggerKey) => TriggerKey = triggerKey;
    internal void SetTriggerInstanceIdInternal(Guid? triggerInstanceId) => TriggerInstanceId = triggerInstanceId;
    internal void SetExtensionsInternal(string[]? extensions) => Extensions = extensions;
    internal void SetUseDaprInternal(bool useDapr) => UseDapr = useDapr;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("domain", out var triggerDomainElement))
            TriggerDomain = triggerDomainElement.GetString() ?? throw new ArgumentException($"Property 'domain' is required for GetInstanceDataTask (Key={Key}).", nameof(config));
        
        if (config.TryGetProperty("flow", out var triggerFlowElement))
            TriggerFlow = triggerFlowElement.GetString() ?? throw new ArgumentException($"Property 'flow' is required for GetInstanceDataTask (Key={Key}).", nameof(config));

        if (config.TryGetProperty("key", out var keyElement))
            TriggerKey = keyElement.GetString();

        if (config.TryGetProperty("instanceId", out var instanceIdElement))
            TriggerInstanceId = instanceIdElement.TryGetGuid(out var instanceId) ? instanceId : null;

        if (config.TryGetProperty("extensions", out var extensionsElement)&&extensionsElement.ValueKind== JsonValueKind.Array)
        {
                var extensionsList = new List<string>();
                foreach (var item in extensionsElement.EnumerateArray())
                {
                    var ext = item.GetString();
                    if (!string.IsNullOrWhiteSpace(ext))
                        extensionsList.Add(ext);
                }
                Extensions = extensionsList.Count > 0 ? extensionsList.ToArray() : null;
        }

        if (config.TryGetProperty("useDapr", out var useDaprElement))
            UseDapr = useDaprElement.GetBoolean();
    }

    public static GetInstanceDataTask Create(JsonElement config)
    {
        return new GetInstanceDataTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current GetInstanceDataTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current GetInstanceDataTask instance.
    /// </summary>
    public GetInstanceDataTask CloneTyped()
    {
        var cloned = new GetInstanceDataTask();
        CopyBaseTo(cloned);

        cloned.TriggerDomain = TriggerDomain;
        cloned.TriggerFlow = TriggerFlow;
        cloned.TriggerKey = TriggerKey;
        cloned.TriggerInstanceId = TriggerInstanceId;
        cloned.Extensions = Extensions;
        cloned.UseDapr = UseDapr;

        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(GetInstanceDataTask source)
    {
        source.CopyBaseToInternal(this);
        SetTriggerDomainInternal(source.TriggerDomain);
        SetTriggerFlowInternal(source.TriggerFlow);
        SetTriggerKeyInternal(source.TriggerKey);
        SetTriggerInstanceIdInternal(source.TriggerInstanceId);
        SetExtensionsInternal(source.Extensions);
        SetUseDaprInternal(source.UseDapr);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        TriggerDomain = string.Empty;
        TriggerFlow = string.Empty;
        TriggerKey = null;
        TriggerInstanceId = null;
        Extensions = null;
        UseDapr = false;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static GetInstanceDataTask CreateEmpty()
    {
        return new GetInstanceDataTask();
    }
}

