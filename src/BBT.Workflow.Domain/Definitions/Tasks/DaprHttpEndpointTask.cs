using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Dapr Http Endpoint Task Definition
/// </summary>
public sealed class DaprHttpEndpointTask : WorkflowTask
{
    private DaprHttpEndpointTask()
    {
        
    }
    
    [JsonConstructor]
    private DaprHttpEndpointTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.DaprHttpEndpoint).ToString();
    }
    
    /// <summary>
    /// References the HTTPEndpoint component name
    /// </summary>
    public string EndpointName { get; private set; } = string.Empty;

    /// <summary>
    /// Path to append to baseUrl
    /// </summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>
    /// HTTP method to use
    /// </summary>
    public string Method { get; private set; } = "GET";
    
    /// <summary>
    /// Body
    /// </summary>
    public JsonElement? Body { get; private set; }
    
    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetEndpointNameInternal(string endpointName) => EndpointName = endpointName;
    internal void SetPathInternal(string path) => Path = path;
    internal void SetMethodInternal(string method) => Method = method;
    internal void SetBodyInternal(JsonElement? body) => Body = body;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("endpointName", out var endpointName))
            EndpointName = endpointName.GetString() ?? throw new ArgumentNullException(nameof(endpointName));

        if (config.TryGetProperty("path", out var path))
            Path = path.GetString() ?? throw new ArgumentNullException(nameof(path));

        if (config.TryGetProperty("method", out var method))
            Method = method.GetString() ?? throw new ArgumentNullException(nameof(method));

        if (config.TryGetProperty("body", out var bodyElement))
        {
            var body = bodyElement.GetRawText();
            Body = string.IsNullOrWhiteSpace(body) ? null : bodyElement;
        }
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }
    
    /// <summary>
    /// Creates a typed deep copy of the current DaprHttpEndpointTask instance.
    /// </summary>
    public DaprHttpEndpointTask CloneTyped()
    {
        var cloned = new DaprHttpEndpointTask();
        CopyBaseTo(cloned);

        cloned.EndpointName = EndpointName;
        cloned.Path = Path;
        cloned.Method = Method;
        cloned.Body = Body;
        
        return cloned;
    }
    
    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(DaprHttpEndpointTask source)
    {
        source.CopyBaseToInternal(this);
        SetEndpointNameInternal(source.EndpointName);
        SetPathInternal(source.Path);
        SetMethodInternal(source.Method);
        SetBodyInternal(source.Body);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        EndpointName = string.Empty;
        Path = string.Empty;
        Method = string.Empty;
        Body = null;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static DaprHttpEndpointTask CreateEmpty()
    {
        return new DaprHttpEndpointTask();
    }

    public static DaprHttpEndpointTask Create(
        JsonElement config)
    {
        return new DaprHttpEndpointTask(config);
    }
    
}