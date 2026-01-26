using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Direct Trigger Task Definition - Executes a transition on a target workflow instance
/// </summary>
public sealed class DirectTriggerTask : WorkflowTask
{
    private DirectTriggerTask()
    {
    }

    [JsonConstructor]
    private DirectTriggerTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.DirectTrigger).ToString();
    }

    /// <summary>
    /// Transition name to execute (required)
    /// </summary>
    public string TransitionName { get; private set; } = string.Empty;

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
    /// Sync of the transition request
    /// </summary>
    public bool TriggerSync { get; private set; } = true;

    /// <summary>
    /// Version of the target workflow (optional)
    /// </summary>
    public string? TriggerVersion { get; private set; }

    /// <summary>
    /// Tags of the target workflow (optional)
    /// </summary>
    public string[]? TriggerTags { get; private set; }

    /// <summary>
    /// Body data to send with the transition request
    /// </summary>
    public JsonElement? Body { get; private set; }

    /// <summary>
    /// Whether to use Dapr service invocation instead of direct HTTP
    /// </summary>
    public bool UseDapr { get; private set; } = false;

    /// <summary>
    /// Whether to validate SSL certificates
    /// </summary>
    public bool ValidateSSL { get; private set; } = true;

    public string? Identifier => TriggerInstanceId.HasValue ? TriggerInstanceId.Value.ToString() : TriggerKey;

    public void SetTags(string[] tags)
    {
        TriggerTags = tags;
    }

    public void SetSync(bool sync)
    {
        TriggerSync = sync;
    }

    public void SetBody(dynamic body)
    {
        Body = JsonSerializer.SerializeToElement(body);
    }

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

    public void SetVersion(string? version)
    {
        TriggerVersion = version;
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

    public void SetTransitionName(string transitionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionName, nameof(transitionName));
        TransitionName = transitionName;
    }

    public void SetUseDapr(bool useDapr)
    {
        UseDapr = useDapr;
    }

    public void SetValidateSSL(bool validateSSL)
    {
        ValidateSSL = validateSSL;
    }

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetTransitionNameInternal(string transitionName) => TransitionName = transitionName;
    internal void SetTriggerDomainInternal(string triggerDomain) => TriggerDomain = triggerDomain;
    internal void SetTriggerFlowInternal(string triggerFlow) => TriggerFlow = triggerFlow;
    internal void SetTriggerKeyInternal(string? triggerKey) => TriggerKey = triggerKey;
    internal void SetTriggerInstanceIdInternal(Guid? triggerInstanceId) => TriggerInstanceId = triggerInstanceId;
    internal void SetBodyInternal(JsonElement? body) => Body = body;
    internal void SetTriggerSyncInternal(bool sync) => TriggerSync = sync;
    internal void SetTriggerTagsInternal(string[]? tags) => TriggerTags = tags;
    internal void SetTriggerVersionInternal(string? version) => TriggerVersion = version;
    internal void SetUseDaprInternal(bool useDapr) => UseDapr = useDapr;
    internal void SetValidateSSLInternal(bool validateSSL) => ValidateSSL = validateSSL;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("transitionName", out var transitionNameElement))
            TransitionName = transitionNameElement.GetString() ?? throw new ArgumentException($"Property 'transitionName' is required for DirectTriggerTask (Key={Key}).", nameof(config));

        if (config.TryGetProperty("domain", out var triggerDomainElement))
            TriggerDomain = triggerDomainElement.GetString() ?? throw new ArgumentException($"Property 'domain' is required for DirectTriggerTask (Key={Key}).", nameof(config));

        if (config.TryGetProperty("flow", out var triggerFlowElement))
            TriggerFlow = triggerFlowElement.GetString() ?? throw new ArgumentException($"Property 'flow' is required for DirectTriggerTask (Key={Key}).", nameof(config));
        
        if (config.TryGetProperty("key", out var keyElement))
            TriggerKey = keyElement.GetString();

        if (config.TryGetProperty("instanceId", out var instanceIdElement))
            TriggerInstanceId = instanceIdElement.TryGetGuid(out var instanceId) ? instanceId : null;

        if (config.TryGetProperty("sync", out var triggerSyncElement))
            TriggerSync = triggerSyncElement.GetBoolean();

        if (config.TryGetProperty("version", out var triggerVersionElement))
            TriggerVersion = triggerVersionElement.GetString();

        if (config.TryGetProperty("tags", out var triggerTagsElement))
            TriggerTags = triggerTagsElement.GetArrayLength() > 0 ? triggerTagsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray() : null;

        if (config.TryGetProperty("body", out var bodyElement))
        {
            var body = bodyElement.GetRawText();
            Body = string.IsNullOrWhiteSpace(body) ? null : bodyElement;
        }

        if (config.TryGetProperty("useDapr", out var useDaprElement))
            UseDapr = useDaprElement.GetBoolean();

        if (config.TryGetProperty("validateSsl", out var validateSslElement))
            ValidateSSL = validateSslElement.GetBoolean();
    }

    public static DirectTriggerTask Create(JsonElement config)
    {
        return new DirectTriggerTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current DirectTriggerTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current DirectTriggerTask instance.
    /// </summary>
    public DirectTriggerTask CloneTyped()
    {
        var cloned = new DirectTriggerTask();
        CopyBaseTo(cloned);

        cloned.TransitionName = TransitionName;
        cloned.TriggerDomain = TriggerDomain;
        cloned.TriggerFlow = TriggerFlow;
        cloned.TriggerKey = TriggerKey;
        cloned.TriggerInstanceId = TriggerInstanceId;
        cloned.Body = Body;
        cloned.TriggerSync = TriggerSync;
        cloned.TriggerVersion = TriggerVersion;
        cloned.TriggerTags = TriggerTags;
        cloned.UseDapr = UseDapr;
        cloned.ValidateSSL = ValidateSSL;
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(DirectTriggerTask source)
    {
        source.CopyBaseToInternal(this);
        SetTransitionNameInternal(source.TransitionName);
        SetTriggerDomainInternal(source.TriggerDomain);
        SetTriggerFlowInternal(source.TriggerFlow);
        SetTriggerKeyInternal(source.TriggerKey);
        SetTriggerInstanceIdInternal(source.TriggerInstanceId);
        SetBodyInternal(source.Body);
        SetTriggerSyncInternal(source.TriggerSync);
        SetTriggerVersionInternal(source.TriggerVersion);
        SetTriggerTagsInternal(source.TriggerTags);
        SetUseDaprInternal(source.UseDapr);
        SetValidateSSLInternal(source.ValidateSSL);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        TransitionName = string.Empty;
        TriggerDomain = string.Empty;
        TriggerFlow = string.Empty;
        TriggerKey = null;
        TriggerInstanceId = null;
        Body = null;
        TriggerSync = true;
        TriggerVersion = null;
        TriggerTags = null;
        UseDapr = false;
        ValidateSSL = true;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static DirectTriggerTask CreateEmpty()
    {
        return new DirectTriggerTask();
    }
}

