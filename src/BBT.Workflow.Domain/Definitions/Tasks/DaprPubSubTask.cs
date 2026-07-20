using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Dapr PubSub Task Definition
/// </summary>
public sealed class DaprPubSubTask : WorkflowTask
{
    private DaprPubSubTask()
    {
        
    }
    
    [JsonConstructor]
    private DaprPubSubTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.DaprPubSub).ToString();
    }
    
    /// <summary>
    /// PubSup name
    /// </summary>
    public string PubSubName { get; private set; } = string.Empty;

    /// <summary>
    /// Topic
    /// </summary>
    public string Topic { get; private set; } = string.Empty;

    /// <summary>
    /// Data
    /// </summary>
    [JsonConverter(typeof(SafeJsonElementConverter))]
    public JsonElement Data { get; private set; }

    /// <summary>
    /// Mete data
    /// </summary>
    [JsonConverter(typeof(SafeJsonElementConverter))]
    public JsonElement Metadata { get; private set; }
    
    public void SetPubSubName(string pubSubName) => PubSubName = pubSubName;
    public void SetTopic(string topic) => Topic = topic;
    public void SetData(dynamic data)
    {
        Data = JsonSerializer.SerializeToElement(data);
    }
    public void SetMetadata(Dictionary<string, string?> metadata)
    {
        Metadata = JsonSerializer.SerializeToElement(metadata);
    }
    
    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetPubSubNameInternal(string pubSubName) => PubSubName = pubSubName;
    internal void SetTopicInternal(string topic) => Topic = topic;
    internal void SetDataInternal(JsonElement data) => Data = data;
    internal void SetMetadataInternal(JsonElement metadata) => Metadata = metadata;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("pubSubName", out var pubSubName))
            PubSubName = pubSubName.GetString() ?? throw new ArgumentNullException(nameof(pubSubName));

        // mapping.
        if (config.TryGetProperty("topic", out var topic))
            Topic = topic.GetString() ?? throw new ArgumentNullException(nameof(topic));

        if (config.TryGetProperty("data", out var data))
            Data = data;

        if (config.TryGetProperty("metadata", out var metadata))
            Metadata = metadata;
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }
    
    /// <summary>
    /// Creates a typed deep copy of the current DaprPubSubTask instance.
    /// </summary>
    public DaprPubSubTask CloneTyped()
    {
        var cloned = new DaprPubSubTask();
        CopyBaseTo(cloned);

        cloned.PubSubName = PubSubName;
        cloned.Topic = Topic;
        cloned.Data = Data;
        cloned.Metadata = Metadata;
        
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(DaprPubSubTask source)
    {
        source.CopyBaseToInternal(this);
        SetPubSubNameInternal(source.PubSubName);
        SetTopicInternal(source.Topic);
        SetDataInternal(source.Data);
        SetMetadataInternal(source.Metadata);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        PubSubName = string.Empty;
        Topic = string.Empty;
        Data = default;
        Metadata = default;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static DaprPubSubTask CreateEmpty()
    {
        return new DaprPubSubTask();
    }

    public static DaprPubSubTask Create(
        JsonElement config)
    {
        return new DaprPubSubTask(config);
    }
}