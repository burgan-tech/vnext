using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Http Task Definition
/// </summary>
public sealed class HttpTask : WorkflowTask
{
    private HttpTask()
    {
    }

    [JsonConstructor]
    private HttpTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.Http).ToString();
    }

    /// <summary>
    /// Url
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Method
    /// </summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// Headers
    /// </summary>
    public JsonElement? Headers { get; private set; }

    /// <summary>
    /// Body
    /// </summary>
    public JsonElement? Body { get; private set; }

    /// <summary>
    /// Timeout seconds
    /// </summary>
    public int TimeoutSeconds { get; private set; } = 30;

    /// <summary>
    /// Validate ssl
    /// </summary>
    public bool ValidateSSL { get; private set; } = true;

    /// <summary>
    /// HTTP status codes that are treated as successful even when they are error codes (e.g. 403, 404).
    /// Supports exact codes ("403") and alias patterns ("4xx", "40x", "5xx", "50x").
    /// When a response status code matches any entry, the task is considered successful
    /// and the ErrorBoundary is not triggered.
    /// </summary>
    public IReadOnlyList<string>? AcceptedStatusCodes { get; private set; }

    public void SetUrl(string url)
    {
        if(string.IsNullOrWhiteSpace(url))
            throw new ArgumentNullException(nameof(url));
        
        Url = url;
    }
    
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
    internal void SetHeadersInternal(JsonElement? headers) => Headers = headers;
    internal void SetBodyInternal(JsonElement? body) => Body = body;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;
    internal void SetValidateSSLInternal(bool validateSSL) => ValidateSSL = validateSSL;
    internal void SetAcceptedStatusCodesInternal(IReadOnlyList<string>? codes) => AcceptedStatusCodes = codes;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("url", out var url))
            Url = url.GetString() ?? throw new ArgumentNullException(nameof(url));

        if (config.TryGetProperty("method", out var method))
            Method = method.GetString() ?? throw new ArgumentNullException(nameof(method));

        if (config.TryGetProperty("headers", out var headersElement))
        {
            var headers = headersElement.GetRawText();
            Headers = string.IsNullOrWhiteSpace(headers) ? null : headersElement;
        }

        if (config.TryGetProperty("body", out var bodyElement))
        {
            var body = bodyElement.GetRawText();
            Body = string.IsNullOrWhiteSpace(body) ? null : bodyElement;
        }

        if (config.TryGetProperty("timeoutSeconds", out var timeoutSeconds))
            TimeoutSeconds = timeoutSeconds.GetInt32();

        if (config.TryGetProperty("validateSsl", out var validateSsl))
            ValidateSSL = validateSsl.GetBoolean();

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

    public static HttpTask Create(
        JsonElement config)
    {
        return new HttpTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current HttpTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current HttpTask instance.
    /// </summary>
    public HttpTask CloneTyped()
    {
        var cloned = new HttpTask();
        CopyBaseTo(cloned);

        cloned.Url = Url;
        cloned.Method = Method;
        cloned.Headers = Headers;
        cloned.Body = Body;
        cloned.TimeoutSeconds = TimeoutSeconds;
        cloned.ValidateSSL = ValidateSSL;
        cloned.AcceptedStatusCodes = AcceptedStatusCodes;

        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(HttpTask source)
    {
        source.CopyBaseToInternal(this);
        Url = source.Url;
        Method = source.Method;
        SetHeadersInternal(source.Headers);
        SetBodyInternal(source.Body);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
        SetValidateSSLInternal(source.ValidateSSL);
        SetAcceptedStatusCodesInternal(source.AcceptedStatusCodes);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        Url = string.Empty;
        Method = "GET";
        Headers = null;
        Body = null;
        TimeoutSeconds = 30;
        ValidateSSL = true;
        AcceptedStatusCodes = null;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static HttpTask CreateEmpty()
    {
        return new HttpTask();
    }
}