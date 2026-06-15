using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Extension definition
/// </summary>
public sealed class Extension : IDomainEntity, IExtensionReference, IReferenceSetter
{
    private Extension()
    {
        Flow = RuntimeSysSchemaInfo.Extensions;
    }

    [JsonConstructor]
    private Extension(
        ExtensionType type,
        ExtensionScope scope,
        OnExecuteTask task
    ): this()
    {
        Type = type;
        Scope = scope;
        Task = task;
    }

    /// <summary>
    /// It is defined to determine under which conditions the relevant extension will work.
    /// </summary>
    public ExtensionType Type { get; private set; }

    /// <summary>
    /// It is defined to determine which services the relevant extension will run on.
    /// </summary>
    public ExtensionScope Scope { get; private set; }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; private set; }

    /// <summary>
    /// Information about which domain the flow is working on and which domain it belongs to.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    [JsonInclude] public OnExecuteTask Task { get; private set; }

    public static string ComponentTypeKey => RuntimeSysSchemaInfo.Extensions;
    public string ComponentKey => ComponentTypeKey;

    public void SetReference(IReference reference)
    {
        Key = Check.NotNullOrWhiteSpace(reference.Key, nameof(Key), ViewConstants.MaxKeyLength);
        Domain = Check.NotNullOrWhiteSpace(reference.Domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
        Version = Check.NotNullOrWhiteSpace(reference.Version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }
}