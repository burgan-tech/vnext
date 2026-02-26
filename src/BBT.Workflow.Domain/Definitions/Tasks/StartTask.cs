using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Start Task Definition - Creates a new workflow instance
/// </summary>
public sealed class StartTask : WorkflowTask
{
    private StartTask()
    {
    }

    [JsonConstructor]
    private StartTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.StartTrigger).ToString();
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
    /// Body data to send with the start request
    /// </summary>
    public JsonElement? Body { get; private set; }

    /// <summary>
    /// Sync of the instance to start
    /// </summary>
    public bool TriggerSync { get; private set; } = true;
    
    /// <summary>
    /// Version of the target workflow
    /// </summary>
    public string? TriggerVersion { get; private set; }

    /// <summary>
    /// Key of the instance to start
    /// </summary>
    public string? TriggerKey { get; private set; }

    /// <summary>
    /// Tags of the instance to start
    /// </summary>
    public string[]? TriggerTags { get; private set; }

    /// <summary>
    /// Whether to use Dapr service invocation instead of direct HTTP
    /// </summary>
    public bool UseDapr { get; private set; } = false;

    /// <summary>
    /// Whether to validate SSL certificates
    /// </summary>
    public bool ValidateSSL { get; private set; } = true;

    /// <summary>
    /// Headers
    /// </summary>
    public JsonElement? Headers { get; private set; }

    /// <summary>
    /// Timeout seconds
    /// </summary>
    public int TimeoutSeconds { get; private set; } = 30;

    public void SetTags(string[] tags)
    {
        TriggerTags = tags;
    }

    public void SetKey(string key)
    {
        TriggerKey = key;
    }

    public void SetBody(dynamic body)
    {
        Body = JsonSerializer.SerializeToElement(body);
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

    public void SetSync(bool sync)
    {
        TriggerSync = sync;
    }
    
    public void SetVersion(string? version)
    {
        TriggerVersion = version;
    }

    public void SetUseDapr(bool useDapr)
    {
        UseDapr = useDapr;
    }

    public void SetValidateSSL(bool validateSSL)
    {
        ValidateSSL = validateSSL;
    }

    public void SetHeaders(Dictionary<string, string?> headers)
    {
        Headers = JsonSerializer.SerializeToElement(headers);
    }

    /// <summary>
    /// Adds or updates a single header by key. If the key already exists, its value is overwritten.
    /// </summary>
    /// <param name="key">The header key. Must not be null or whitespace.</param>
    /// <param name="value">The header value; can be null.</param>
    public void AddHeader(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var d = TaskHeadersHelper.ToMutableDictionary(Headers);
        d[key] = value;
        Headers = TaskHeadersHelper.FromDictionary(d);
    }

    /// <summary>
    /// Removes a header by key. Does nothing if the key does not exist.
    /// </summary>
    /// <param name="key">The header key to remove. Must not be null or whitespace.</param>
    public void RemoveHeader(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var d = TaskHeadersHelper.ToMutableDictionary(Headers);
        d.Remove(key);
        Headers = TaskHeadersHelper.FromDictionary(d);
    }

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetTriggerDomainInternal(string triggerDomain) => TriggerDomain = triggerDomain;

    internal void SetTriggerFlowInternal(string triggerFlow) => TriggerFlow = triggerFlow;
    internal void SetBodyInternal(JsonElement? body) => Body = body;
    internal void SetTriggerSyncInternal(bool sync) => TriggerSync = sync;
    internal void SetTriggerVersionInternal(string? version) => TriggerVersion = version;
    internal void SetTriggerKeyInternal(string? key) => TriggerKey = key;
    internal void SetTriggerTagsInternal(string[]? tags) => TriggerTags = tags;
    internal void SetUseDaprInternal(bool useDapr) => UseDapr = useDapr;
    internal void SetValidateSSLInternal(bool validateSSL) => ValidateSSL = validateSSL;
    internal void SetHeadersInternal(JsonElement? headers) => Headers = headers;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("domain", out var triggerDomainElement))
            TriggerDomain = triggerDomainElement.GetString() ?? throw new ArgumentException($"Property 'domain' is required for StartTask (Key={Key}).", nameof(config));
        
        if (config.TryGetProperty("flow", out var triggerFlowElement))
            TriggerFlow = triggerFlowElement.GetString() ?? throw new ArgumentException($"Property 'flow' is required for StartTask (Key={Key}).", nameof(config));
        
        if (config.TryGetProperty("body", out var bodyElement))
        {
            var body = bodyElement.GetRawText();
            Body = string.IsNullOrWhiteSpace(body) ? null : bodyElement;
        }
        
        if (config.TryGetProperty("sync", out var triggerSyncElement))
            TriggerSync = triggerSyncElement.GetBoolean();
        
        if (config.TryGetProperty("version", out var triggerVersionElement))
            TriggerVersion = triggerFlowElement.GetString() ?? string.Empty;

        if (config.TryGetProperty("key", out var triggerKeyElement))
            TriggerKey = triggerKeyElement.GetString() ?? string.Empty;

        if (config.TryGetProperty("tags", out var triggerTagsElement))
            TriggerTags = triggerTagsElement.GetArrayLength() > 0 ? triggerTagsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray() : null;

        if (config.TryGetProperty("useDapr", out var useDaprElement))
            UseDapr = useDaprElement.GetBoolean();

        if (config.TryGetProperty("validateSsl", out var validateSslElement))
            ValidateSSL = validateSslElement.GetBoolean();

        if (config.TryGetProperty("headers", out var headersElement))
        {
            var headers = headersElement.GetRawText();
            Headers = string.IsNullOrWhiteSpace(headers) ? null : headersElement;
        }

        if (config.TryGetProperty("timeoutSeconds", out var timeoutSeconds))
            TimeoutSeconds = timeoutSeconds.GetInt32();
    }

    public static StartTask Create(JsonElement config)
    {
        return new StartTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current StartTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current StartTask instance.
    /// </summary>
    public StartTask CloneTyped()
    {
        var cloned = new StartTask();
        CopyBaseTo(cloned);

        cloned.TriggerDomain = TriggerDomain;
        cloned.TriggerFlow = TriggerFlow;
        cloned.Body = Body;
        cloned.TriggerSync = TriggerSync;
        cloned.TriggerVersion = TriggerVersion;
        cloned.TriggerKey = TriggerKey;
        cloned.TriggerTags = TriggerTags;
        cloned.UseDapr = UseDapr;
        cloned.ValidateSSL = ValidateSSL;
        cloned.Headers = Headers;
        cloned.TimeoutSeconds = TimeoutSeconds;
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(StartTask source)
    {
        source.CopyBaseToInternal(this);
        SetTriggerDomainInternal(source.TriggerDomain);
        SetTriggerFlowInternal(source.TriggerFlow);
        SetBodyInternal(source.Body);
        SetTriggerSyncInternal(source.TriggerSync);
        SetTriggerVersionInternal(source.TriggerVersion);
        SetTriggerKeyInternal(source.TriggerKey);
        SetTriggerTagsInternal(source.TriggerTags);
        SetUseDaprInternal(source.UseDapr);
        SetValidateSSLInternal(source.ValidateSSL);
        SetHeadersInternal(source.Headers);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        TriggerDomain = string.Empty;
        TriggerFlow = string.Empty;
        Body = null;
        TriggerSync = true;
        TriggerVersion = null;  
        TriggerKey = null;
        TriggerTags = null;
        UseDapr = false;
        ValidateSSL = true;
        Headers = null;
        TimeoutSeconds = 30;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static StartTask CreateEmpty()
    {
        return new StartTask();
    }
}