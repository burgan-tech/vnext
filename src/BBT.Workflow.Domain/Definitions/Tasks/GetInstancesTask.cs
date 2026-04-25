using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Get Instances Task Definition - Retrieves a list of instance data from a workflow
/// by calling the data function endpoint with pagination and filtering support.
/// </summary>
public sealed class GetInstancesTask : WorkflowTask
{
    private GetInstancesTask()
    {
    }

    [JsonConstructor]
    private GetInstancesTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.GetInstances).ToString();
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
    /// Page number for pagination (1-based)
    /// </summary>
    public int Page { get; private set; } = 1;

    /// <summary>
    /// Page size for pagination
    /// </summary>
    public int PageSize { get; private set; } = 10;

    /// <summary>
    /// Sort field and direction (e.g., "-CreatedAt" for descending)
    /// </summary>
    public string? Sort { get; private set; }

    /// <summary>
    /// Filter expression to apply to the query (JSON format)
    /// </summary>
    public string? Filter { get; private set; }

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

    public void SetPage(int page)
    {
        Page = page > 0 ? page : 1;
    }

    public void SetPageSize(int pageSize)
    {
        PageSize = pageSize > 0 ? pageSize : 10;
    }

    public void SetSort(string? sort)
    {
        Sort = sort;
    }

    public void SetFilter(string? filter)
    {
        Filter = filter;
    }
    
    public void SetFilter(object? filter)
    {
        Filter = JsonSerializer.Serialize(filter);
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
    internal void SetPageInternal(int page) => Page = page;
    internal void SetPageSizeInternal(int pageSize) => PageSize = pageSize;
    internal void SetSortInternal(string? sort) => Sort = sort;
    internal void SetFilterInternal(string? filter) => Filter = filter;
    internal void SetUseDaprInternal(bool useDapr) => UseDapr = useDapr;
    internal void SetValidateSSLInternal(bool validateSSL) => ValidateSSL = validateSSL;
    internal void SetHeadersInternal(JsonElement? headers) => Headers = headers;
    internal void SetTimeoutSecondsInternal(int timeoutSeconds) => TimeoutSeconds = timeoutSeconds;
    internal void SetAcceptedStatusCodesInternal(IReadOnlyList<string>? codes) => AcceptedStatusCodes = codes;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("domain", out var triggerDomainElement))
            TriggerDomain = triggerDomainElement.GetString() ?? throw new ArgumentException($"Property 'domain' is required for GetInstancesTask (Key={Key}).", nameof(config));

        if (config.TryGetProperty("flow", out var triggerFlowElement))
            TriggerFlow = triggerFlowElement.GetString() ?? throw new ArgumentException($"Property 'flow' is required for GetInstancesTask (Key={Key}).", nameof(config));

        if (config.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var page))
            Page = page > 0 ? page : 1;

        if (config.TryGetProperty("pageSize", out var pageSizeElement) && pageSizeElement.TryGetInt32(out var pageSize))
            PageSize = pageSize > 0 ? pageSize : 10;

        if (config.TryGetProperty("sort", out var sortElement))
            Sort = sortElement.GetString();

        if (config.TryGetProperty("filter", out var filterElement))
        {
            if (filterElement.ValueKind == JsonValueKind.String)
            {
                Filter = filterElement.GetString();
            }
            else if (filterElement.ValueKind == JsonValueKind.Array)
            {
                var filterList = new List<string>();
                foreach (var item in filterElement.EnumerateArray())
                {
                    var filterValue = item.GetString();
                    if (!string.IsNullOrWhiteSpace(filterValue))
                        filterList.Add(filterValue);
                }
                Filter = filterList.Count > 0 ? filterList[0] : null;
            }
        }

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

    public static GetInstancesTask Create(JsonElement config)
    {
        return new GetInstancesTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current GetInstancesTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current GetInstancesTask instance.
    /// </summary>
    public GetInstancesTask CloneTyped()
    {
        var cloned = new GetInstancesTask();
        CopyBaseTo(cloned);

        cloned.TriggerDomain = TriggerDomain;
        cloned.TriggerFlow = TriggerFlow;
        cloned.Page = Page;
        cloned.PageSize = PageSize;
        cloned.Sort = Sort;
        cloned.Filter = Filter;
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
    public void CopyFromInternal(GetInstancesTask source)
    {
        source.CopyBaseToInternal(this);
        SetTriggerDomainInternal(source.TriggerDomain);
        SetTriggerFlowInternal(source.TriggerFlow);
        SetPageInternal(source.Page);
        SetPageSizeInternal(source.PageSize);
        SetSortInternal(source.Sort);
        SetFilterInternal(source.Filter);
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
        Page = 1;
        PageSize = 10;
        Sort = null;
        Filter = null;
        UseDapr = false;
        ValidateSSL = true;
        Headers = null;
        TimeoutSeconds = 30;
        AcceptedStatusCodes = null;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static GetInstancesTask CreateEmpty()
    {
        return new GetInstancesTask();
    }
}
