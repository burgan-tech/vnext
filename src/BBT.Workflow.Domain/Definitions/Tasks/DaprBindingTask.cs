using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Dapr Binding Task Definition
/// </summary>
public sealed class DaprBindingTask : WorkflowTask
{
    private DaprBindingTask()
    {
        
    }
    
    [JsonConstructor]
    private DaprBindingTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.DaprBinding).ToString();
    }
    /// <summary>
    /// Binding name
    /// </summary>
    public string BindingName { get; private set; } = string.Empty;

    /// <summary>
    /// Operation
    /// </summary>
    public string Operation { get; private set; } = string.Empty;

    /// <summary>
    /// Mete data
    /// </summary>
    [JsonConverter(typeof(SafeJsonElementConverter))]
    public JsonElement Metadata { get; private set; }

    /// <summary>
    /// Data
    /// </summary>
    public JsonElement? Data { get; private set; }

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetBindingNameInternal(string bindingName) => BindingName = bindingName;
    internal void SetOperationInternal(string operation) => Operation = operation;
    internal void SetMetadataInternal(JsonElement metadata) => Metadata = metadata;
    internal void SetDataInternal(JsonElement? data) => Data = data;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("bindingName", out var bindingName))
            BindingName = bindingName.GetString() ?? throw new ArgumentNullException(nameof(bindingName));

        if (config.TryGetProperty("operation", out var operation))
            Operation = operation.GetString() ?? throw new ArgumentNullException(nameof(operation));

        if (config.TryGetProperty("metadata", out var metadata))
            Metadata = metadata;

        if (config.TryGetProperty("data", out var data))
            Data = data;
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }
    
    /// <summary>
    /// Creates a typed deep copy of the current DaprBindingTask instance.
    /// </summary>
    public DaprBindingTask CloneTyped()
    {
        var cloned = new DaprBindingTask();
        CopyBaseTo(cloned);

        cloned.BindingName = BindingName;
        cloned.Operation = Operation;
        cloned.Metadata = Metadata;
        cloned.Data = Data;
        
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(DaprBindingTask source)
    {
        source.CopyBaseToInternal(this);
        SetBindingNameInternal(source.BindingName);
        SetOperationInternal(source.Operation);
        SetMetadataInternal(source.Metadata);
        SetDataInternal(source.Data);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        BindingName = string.Empty;
        Operation = string.Empty;
        Metadata = default;
        Data = default;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static DaprBindingTask CreateEmpty()
    {
        return new DaprBindingTask();
    }

    public static DaprBindingTask Create(
        JsonElement config)
    {
        return new DaprBindingTask(config);
    }
    
}