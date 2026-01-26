using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Base Task
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DaprHttpEndpointTask), typeDiscriminator: "1")]
[JsonDerivedType(typeof(DaprBindingTask), typeDiscriminator: "2")]
[JsonDerivedType(typeof(DaprServiceTask), typeDiscriminator: "3")]
[JsonDerivedType(typeof(DaprPubSubTask), typeDiscriminator: "4")]
[JsonDerivedType(typeof(HumanTask), typeDiscriminator: "5")]
[JsonDerivedType(typeof(HttpTask), typeDiscriminator: "6")]
[JsonDerivedType(typeof(ScriptTask), typeDiscriminator: "7")]
[JsonDerivedType(typeof(NotificationTask), typeDiscriminator: "10")]
[JsonDerivedType(typeof(StartTask), typeDiscriminator: "11")]
[JsonDerivedType(typeof(DirectTriggerTask), typeDiscriminator: "12")]
[JsonDerivedType(typeof(GetInstanceDataTask), typeDiscriminator: "13")]
[JsonDerivedType(typeof(SubProcessTask), typeDiscriminator: "14")]
[JsonDerivedType(typeof(GetInstancesTask), typeDiscriminator: "15")]
public abstract class WorkflowTask : IDomainEntity, ITaskReference, IReferenceSetter, ITaskClonable
{
    protected WorkflowTask()
    {

    }

    [JsonConstructor]
    protected WorkflowTask(
        JsonElement config
    ) : this()
    {
        Configure(config);
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; init; } = RuntimeSysSchemaInfo.Tasks;

    /// <summary>
    /// Information about which domain the flow is working on and which domain it belongs to.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    /// <summary>
    /// <see cref="TaskType"/>
    /// </summary>
    [JsonPropertyOrder(0)]
    [JsonIgnore]
    public string Type { get; protected set; }

    /// <summary>
    /// Configuration
    /// </summary>
    public JsonElement Config { get; private set; }

    public string ComponentKey => RuntimeSysSchemaInfo.Tasks;

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), TaskConstants.MaxKeyLength);
    }

    private void SetDomain(string domain)
    {
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
    }

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    /// <summary>
    /// Internal method for object pooling - sets properties directly without validation
    /// </summary>
    internal void SetKeyInternal(string key) => Key = key;

    /// <summary>
    /// Internal method for object pooling - sets properties directly without validation
    /// </summary>
    internal void SetDomainInternal(string domain) => Domain = domain;

    /// <summary>
    /// Internal method for object pooling - sets properties directly without validation
    /// </summary>
    internal void SetVersionInternal(string version) => Version = version;

    /// <summary>
    /// Internal method for object pooling - sets properties directly without validation
    /// </summary>
    internal void SetConfigInternal(JsonElement config) => Config = config;

    public TaskType GetTaskType() => Enum.Parse<TaskType>(Type);

    protected virtual void Configure(JsonElement config)
    {
        Config = config;
    }

    public void SetReference(IReference reference)
    {
        SetKey(reference.Key);
        SetDomain(reference.Domain);
        SetVersion(reference.Version);
    }

    /// <summary>
    /// Creates a deep copy of the current task instance.
    /// Each derived class must implement its own cloning logic for optimal performance.
    /// </summary>
    /// <returns>A new instance of the task with identical configuration but separate state.</returns>
    public abstract WorkflowTask Clone();

    /// <summary>
    /// Helper method for derived classes to copy base properties.
    /// </summary>
    /// <param name="target">The target task instance to copy properties to.</param>
    protected void CopyBaseTo(WorkflowTask target)
    {
        target.SetKey(Key);
        target.SetDomain(Domain);
        target.SetVersion(Version);
        target.Type = Type;
        target.Config = Config;
    }

    /// <summary>
    /// Internal method for object pooling - copies base properties directly for better performance
    /// </summary>
    /// <param name="target">The target task instance to copy properties to.</param>
    public void CopyBaseToInternal(WorkflowTask target)
    {
        target.SetKeyInternal(Key);
        target.SetDomainInternal(Domain);
        target.SetVersionInternal(Version);
        target.Type = Type;
        target.SetConfigInternal(Config);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling.
    /// Derived classes should override this to reset their specific properties.
    /// </summary>
    public virtual void Reset()
    {
        Key = string.Empty;
        Domain = string.Empty;
        Version = string.Empty;
        Type = string.Empty;
        Config = default;
    }
}