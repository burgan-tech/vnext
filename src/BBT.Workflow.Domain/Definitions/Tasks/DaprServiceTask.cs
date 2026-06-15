using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Dapr Service Task Definition
/// </summary>
public sealed class DaprServiceTask : WorkflowTask
{
    private DaprServiceTask()
    {
        
    }
    
    [JsonConstructor]
    private DaprServiceTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.DaprService).ToString();
    }
    
    /// <summary>
    /// App ID
    /// </summary>
    public string AppId { get; private set; } = string.Empty;

    /// <summary>
    /// Method Name
    /// </summary>
    public string MethodName { get; private set; } = string.Empty;

    /// <summary>
    /// Http Verb
    /// </summary>
    public string HttpVerb { get; private set; } = string.Empty;

    /// <summary>
    /// Body
    /// </summary>
    public JsonElement? Body { get; private set; }

    /// <summary>
    /// Headers
    /// </summary>
    public JsonElement? Headers { get; private set; }

    /// <summary>
    /// Query String
    /// </summary>
    public string? QueryString { get; private set; }

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

    public void SetAppId(string appId) => AppId = appId;
    public void SetMethodName(string methodName) => MethodName = methodName;
    public void SetQueryString(string? queryString) => QueryString = queryString;
    public void SetBody(dynamic body)
    {
        Body = JsonSerializer.SerializeToElement(body);
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
    internal void SetAppIdInternal(string appId) => AppId = appId;
    internal void SetMethodNameInternal(string methodName) => MethodName = methodName;
    internal void SetHttpVerbInternal(string httpVerb) => HttpVerb = httpVerb;
    internal void SetBodyInternal(JsonElement? body) => Body = body;
    internal void SetQueryStringInternal(string? queryString) => QueryString = queryString;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;
    internal void SetAcceptedStatusCodesInternal(IReadOnlyList<string>? codes) => AcceptedStatusCodes = codes;

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetHeadersInternal(JsonElement? headers) => Headers = headers;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("appId", out var appId))
            AppId = appId.GetString() ?? throw new ArgumentNullException(nameof(appId));

        if (config.TryGetProperty("methodName", out var methodName))
            MethodName = methodName.GetString() ?? throw new ArgumentNullException(nameof(methodName));

        if (config.TryGetProperty("httpVerb", out var httpVerb))
            HttpVerb = httpVerb.GetString() ?? throw new ArgumentNullException(nameof(httpVerb));

        if (config.TryGetProperty("body", out var body))
            Body = body;

        if (config.TryGetProperty("queryString", out var queryString))
            QueryString = queryString.GetString();

        if (config.TryGetProperty("timeoutSeconds", out var timeout))
            TimeoutSeconds = timeout.GetInt32();

        if (config.TryGetProperty("headers", out var headersElement))
        {
            var headers = headersElement.GetRawText();
            Headers = string.IsNullOrWhiteSpace(headers) ? null : headersElement;
        }

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
    
    public static DaprServiceTask Create(
        JsonElement config)
    {
        return new DaprServiceTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current DaprServiceTask instance.
    /// This implementation uses direct property copying for optimal performance.
    /// </summary>
    /// <returns>A new DaprServiceTask instance with identical configuration.</returns>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current DaprServiceTask instance.
    /// </summary>
    /// <returns>A new DaprServiceTask instance with identical configuration.</returns>
    public DaprServiceTask CloneTyped()
    {
        var cloned = new DaprServiceTask();
        CopyBaseTo(cloned);
        
        // Copy DaprServiceTask specific properties
        cloned.AppId = AppId;
        cloned.MethodName = MethodName; // This will be the original method name from cache
        cloned.HttpVerb = HttpVerb;
        cloned.Body = Body; // JsonElement is a struct, so this is safe
        cloned.QueryString = QueryString;
        cloned.TimeoutSeconds = TimeoutSeconds;
        cloned.Headers = Headers;
        cloned.AcceptedStatusCodes = AcceptedStatusCodes;
        
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(DaprServiceTask source)
    {
        source.CopyBaseToInternal(this);
        SetAppIdInternal(source.AppId);
        SetMethodNameInternal(source.MethodName);
        SetHttpVerbInternal(source.HttpVerb);
        SetBodyInternal(source.Body);
        SetQueryStringInternal(source.QueryString);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
        SetHeadersInternal(source.Headers);
        SetAcceptedStatusCodesInternal(source.AcceptedStatusCodes);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        AppId = string.Empty;
        MethodName = string.Empty;
        HttpVerb = string.Empty;
        Body = null;
        QueryString = null;
        TimeoutSeconds = 30;
        Headers = null;
        AcceptedStatusCodes = null;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static DaprServiceTask CreateEmpty()
    {
        return new DaprServiceTask();
    }
}