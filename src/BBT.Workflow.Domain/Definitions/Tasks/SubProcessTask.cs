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
    /// Sync of the instance to start
    /// </summary>
    public bool TriggerSync { get; private set; } = false;

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

    /// <summary>
    /// HTTP status codes that are treated as successful even when they are error codes (e.g. 403, 404).
    /// Supports exact codes ("403") and alias patterns ("4xx", "40x", "5xx", "50x").
    /// When a response status code matches any entry, the task is considered successful
    /// and the ErrorBoundary is not triggered.
    /// </summary>
    public IReadOnlyList<string>? AcceptedStatusCodes { get; private set; }

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

    public void SetValidateSSL(bool validateSSL)
    {
        ValidateSSL = validateSSL;
    }
    
    public void SetSync(bool sync)
    {
        TriggerSync = sync;
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
    internal void SetTriggerKeyInternal(string? triggerKey) => TriggerKey = triggerKey;
    internal void SetTriggerVersionInternal(string? triggerVersion) => TriggerVersion = triggerVersion;
    internal void SetTriggerSyncInternal(bool sync) => TriggerSync = sync;
    internal void SetBodyInternal(JsonElement? body) => Body = body;
    internal void SetTriggerTagsInternal(string[]? tags) => TriggerTags = tags;
    internal void SetUseDaprInternal(bool useDapr) => UseDapr = useDapr;
    internal void SetValidateSSLInternal(bool validateSSL) => ValidateSSL = validateSSL;
    internal void SetHeadersInternal(JsonElement? headers) => Headers = headers;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;
    internal void SetAcceptedStatusCodesInternal(IReadOnlyList<string>? codes) => AcceptedStatusCodes = codes;

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
        
        if (config.TryGetProperty("sync", out var triggerSyncElement))
            TriggerSync = triggerSyncElement.GetBoolean();

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

        if (config.TryGetProperty("validateSsl", out var validateSslElement))
            ValidateSSL = validateSslElement.GetBoolean();

        if (config.TryGetProperty("headers", out var headersElement))
        {
            var headers = headersElement.GetRawText();
            Headers = string.IsNullOrWhiteSpace(headers) ? null : headersElement;
        }

        if (config.TryGetProperty("timeoutSeconds", out var timeoutSeconds))
            TimeoutSeconds = timeoutSeconds.GetInt32();

        if (config.TryGetProperty("acceptedStatusCodes", out var acceptedCodesElement) &&
            acceptedCodesElement.ValueKind == JsonValueKind.Array)
        {
            var codes = acceptedCodesElement.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
            AcceptedStatusCodes = codes.Count > 0 ? codes : null;
        }
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
        cloned.TriggerSync = TriggerSync;
        cloned.Body = Body;
        cloned.TriggerTags = TriggerTags;
        cloned.UseDapr = UseDapr;
        cloned.ValidateSSL = ValidateSSL;
        cloned.Headers = Headers;
        cloned.TimeoutSeconds = TimeoutSeconds;
        cloned.AcceptedStatusCodes = AcceptedStatusCodes;
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
        SetTriggerSyncInternal(source.TriggerSync);
        SetBodyInternal(source.Body);
        SetTriggerTagsInternal(source.TriggerTags);
        SetUseDaprInternal(source.UseDapr);
        SetValidateSSLInternal(source.ValidateSSL);
        SetHeadersInternal(source.Headers);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
        SetAcceptedStatusCodesInternal(source.AcceptedStatusCodes);
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
        TriggerSync = false;
        Body = null;
        TriggerTags = null;
        UseDapr = false;
        ValidateSSL = true;
        Headers = null;
        TimeoutSeconds = 30;
        AcceptedStatusCodes = null;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static SubProcessTask CreateEmpty()
    {
        return new SubProcessTask();
    }
}