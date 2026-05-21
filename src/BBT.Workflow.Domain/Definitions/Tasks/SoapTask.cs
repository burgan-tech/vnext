using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace BBT.Workflow.Definitions;

/// <summary>
/// SOAP Task Definition — sends a SOAP 1.1 or 1.2 request to a web service endpoint.
/// Body is stored as a raw XML string (not JsonElement) to support SOAP envelope templates.
/// </summary>
public sealed class SoapTask : WorkflowTask
{
    private SoapTask()
    {
    }

    [JsonConstructor]
    private SoapTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.Soap).ToString();
    }

    /// <summary>Target SOAP endpoint URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// SOAPAction value. Required for SOAP 1.1 (sent as HTTP header).
    /// For SOAP 1.2 it is embedded in the Content-Type action parameter.
    /// </summary>
    public string SoapAction { get; set; } = string.Empty;

    /// <summary>SOAP protocol version: "1.1" or "1.2". Defaults to "1.1".</summary>
    public string SoapVersion { get; private set; } = "1.1";

    /// <summary>
    /// SOAP envelope XML body as a raw string template.
    /// Unlike HttpTask, this is a plain string — not JsonElement — to support arbitrary XML.
    /// </summary>
    public string? Body { get; private set; }

    /// <summary>Additional HTTP headers (e.g. Authorization). Stored as JSON object.</summary>
    public JsonElement? Headers { get; private set; }

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; private set; } = 30;

    /// <summary>Whether to validate the server SSL certificate.</summary>
    public bool ValidateSSL { get; private set; } = true;

    /// <summary>
    /// HTTP status codes treated as successful even when they indicate errors.
    /// SOAP faults are typically returned over HTTP 500, so ["500"] is a common value here.
    /// Supports exact codes ("500") and wildcard patterns ("5xx", "50x").
    /// </summary>
    public IReadOnlyList<string>? AcceptedStatusCodes { get; private set; }

    public void SetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentNullException(nameof(url));

        Url = url;
    }

    public void SetBody(string? body)
    {
        Body = body;
    }

    /// <summary>Serializes <paramref name="xmlDoc"/> to its outer XML and sets it as the request body.</summary>
    public void SetBody(XmlDocument? xmlDoc)
    {
        Body = xmlDoc?.OuterXml;
    }

    public void SetSoapAction(string soapAction)
    {
        SoapAction = soapAction ?? string.Empty;
    }

    public void SetHeaders(Dictionary<string, string?> headers)
    {
        Headers = JsonSerializer.SerializeToElement(headers);
    }

    /// <summary>Adds or updates a single header. Overwrites existing values for the same key.</summary>
    public void AddHeader(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var d = TaskHeadersHelper.ToMutableDictionary(Headers);
        d[key] = value;
        Headers = TaskHeadersHelper.FromDictionary(d);
    }

    /// <summary>Removes a header by key. Does nothing if the key does not exist.</summary>
    public void RemoveHeader(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var d = TaskHeadersHelper.ToMutableDictionary(Headers);
        d.Remove(key);
        Headers = TaskHeadersHelper.FromDictionary(d);
    }

    // Internal setters for object pooling
    internal void SetBodyInternal(string? body) => Body = body;
    internal void SetSoapVersionInternal(string soapVersion) => SoapVersion = soapVersion;
    internal void SetSoapActionInternal(string soapAction) => SoapAction = soapAction;
    internal void SetHeadersInternal(JsonElement? headers) => Headers = headers;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;
    internal void SetValidateSSLInternal(bool validateSSL) => ValidateSSL = validateSSL;
    internal void SetAcceptedStatusCodesInternal(IReadOnlyList<string>? codes) => AcceptedStatusCodes = codes;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("url", out var url))
            Url = url.GetString() ?? throw new ArgumentNullException(nameof(url));

        if (config.TryGetProperty("soapAction", out var soapAction))
            SoapAction = soapAction.GetString() ?? string.Empty;

        if (config.TryGetProperty("soapVersion", out var soapVersion))
            SoapVersion = soapVersion.GetString() ?? "1.1";

        if (config.TryGetProperty("body", out var body))
            Body = body.ValueKind == JsonValueKind.String ? body.GetString() : null;

        if (config.TryGetProperty("headers", out var headersElement))
        {
            var raw = headersElement.GetRawText();
            Headers = string.IsNullOrWhiteSpace(raw) ? null : headersElement;
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

    public static SoapTask Create(JsonElement config)
    {
        return new SoapTask(config);
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    public SoapTask CloneTyped()
    {
        var cloned = new SoapTask();
        CopyBaseTo(cloned);

        cloned.Url = Url;
        cloned.SoapAction = SoapAction;
        cloned.SoapVersion = SoapVersion;
        cloned.Body = Body;
        cloned.Headers = Headers;
        cloned.TimeoutSeconds = TimeoutSeconds;
        cloned.ValidateSSL = ValidateSSL;
        cloned.AcceptedStatusCodes = AcceptedStatusCodes;

        return cloned;
    }

    public void CopyFromInternal(SoapTask source)
    {
        source.CopyBaseToInternal(this);
        Url = source.Url;
        SetSoapActionInternal(source.SoapAction);
        SetSoapVersionInternal(source.SoapVersion);
        SetBodyInternal(source.Body);
        SetHeadersInternal(source.Headers);
        SetTimeoutSecondsInternal(source.TimeoutSeconds);
        SetValidateSSLInternal(source.ValidateSSL);
        SetAcceptedStatusCodesInternal(source.AcceptedStatusCodes);
    }

    public override void Reset()
    {
        base.Reset();
        Url = string.Empty;
        SoapAction = string.Empty;
        SoapVersion = "1.1";
        Body = null;
        Headers = null;
        TimeoutSeconds = 30;
        ValidateSSL = true;
        AcceptedStatusCodes = null;
    }

    public static SoapTask CreateEmpty()
    {
        return new SoapTask();
    }
}
