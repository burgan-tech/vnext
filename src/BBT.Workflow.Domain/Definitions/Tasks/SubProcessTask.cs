using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// SubProcess Task Definition - Triggers transitions on correlated SubFlow instances
/// </summary>
public sealed class SubProcessTask : WorkflowTask
{
    private SubProcessTask()
    {
    }

    [JsonConstructor]
    private SubProcessTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.SubProcess).ToString();
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
    /// Key of the instance to start
    /// </summary>
    public string? TriggerKey { get; private set; }

    /// <summary>
    /// SubFlow version (optional)
    /// </summary>
    public string? TriggerVersion { get; private set; }

    /// <summary>
    /// Body data to send with the subprocess request
    /// </summary>
    public JsonElement? Body { get; private set; }

    /// <summary>
    /// Tags of the instance to start
    /// </summary>
    public string[]? TriggerTags { get; private set; }

    /// <summary>
    /// Whether to use Dapr service invocation instead of direct HTTP
    /// </summary>
    public bool UseDapr { get; private set; } = false;

    public void SetBody(dynamic body)
    {
        Body = JsonSerializer.SerializeToElement(body);
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

    public void SetVersion(string version)
    {
        TriggerVersion = version;
    }

    public void SetTags(string[] tags)
    {
        TriggerTags = tags;
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
    internal void SetTriggerVersionInternal(string? triggerVersion) => TriggerVersion = triggerVersion;
    internal void SetBodyInternal(JsonElement? body) => Body = body;
    internal void SetTriggerTagsInternal(string[]? tags) => TriggerTags = tags;
    internal void SetUseDaprInternal(bool useDapr) => UseDapr = useDapr;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("domain", out var triggerDomainElement))
            TriggerDomain = triggerDomainElement.GetString() ?? throw new ArgumentException($"Property 'domain' is required for SubProcessTask (Key={Key}).", nameof(config));
        
        if (config.TryGetProperty("flow", out var triggerFlowElement))
            TriggerFlow = triggerFlowElement.GetString() ?? throw new ArgumentException($"Property 'flow' is required for SubProcessTask (Key={Key}).", nameof(config));

        if (config.TryGetProperty("key", out var keyElement))
            TriggerKey = keyElement.GetString() ?? string.Empty;

        if (config.TryGetProperty("version", out var versionElement))
            TriggerVersion = versionElement.GetString();

        if (config.TryGetProperty("body", out var bodyElement))
        {
            var body = bodyElement.GetRawText();
            Body = string.IsNullOrWhiteSpace(body) ? null : bodyElement;
        }
        
        if (config.TryGetProperty("tags", out var triggerTagsElement))
            TriggerTags = triggerTagsElement.GetArrayLength() > 0
                ? triggerTagsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : null;

        if (config.TryGetProperty("useDapr", out var useDaprElement))
            UseDapr = useDaprElement.GetBoolean();
    }

    public static SubProcessTask Create(JsonElement config)
    {
        return new SubProcessTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current SubProcessTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current SubProcessTask instance.
    /// </summary>
    public SubProcessTask CloneTyped()
    {
        var cloned = new SubProcessTask();
        CopyBaseTo(cloned);

        cloned.TriggerDomain = TriggerDomain;
        cloned.TriggerFlow = TriggerFlow;
        cloned.TriggerKey = TriggerKey;
        cloned.TriggerVersion = TriggerVersion;
        cloned.Body = Body;
        cloned.TriggerTags = TriggerTags;
        cloned.UseDapr = UseDapr;
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(SubProcessTask source)
    {
        source.CopyBaseToInternal(this);
        SetTriggerDomainInternal(source.TriggerDomain);
        SetTriggerFlowInternal(source.TriggerFlow);
        SetTriggerKeyInternal(source.TriggerKey);
        SetTriggerVersionInternal(source.TriggerVersion);
        SetBodyInternal(source.Body);
        SetTriggerTagsInternal(source.TriggerTags);
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
        TriggerVersion = null;
        Body = null;
        TriggerTags = null;
        UseDapr = false;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static SubProcessTask CreateEmpty()
    {
        return new SubProcessTask();
    }
}