using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// These are function definitions that will be distributed with the flow.
/// In general, BFF and calculation methods are defined as functions.
/// </summary>
public sealed class Function : IDomainEntity, IFunctionReference, IReferenceSetter
{
    private Function()
    {
        Flow = RuntimeSysSchemaInfo.Functions;
    }

    [JsonConstructor]
    public Function(
        TaskScope scope,
        OnExecuteTask task,
        List<RoleGrant>? roles = null
    ) : this()
    {
        Scope = scope;
        Task = task;
        this.roles = roles ?? [];
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// This is information about the domain on which the stream where the record is located.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; init; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    public TaskScope Scope { get; private set; }
    [JsonInclude] public OnExecuteTask Task { get; private set; }

    [JsonInclude] [JsonPropertyName("roles")]
    private List<RoleGrant> roles = new();

    /// <summary>
    /// Function roles for authorization (domain-qualified). DENY always overrides ALLOW.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> Roles => roles.AsReadOnly();

    public string ComponentKey => RuntimeSysSchemaInfo.Functions;

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), FunctionConstants.MaxKeyLength);
    }

    private void SetDomain(string domain)
    {
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
    }

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    public List<OnExecuteTask> GetExecuteTasks()
    {
       return
       [
           Task
       ];
    }
    public void SetReference(IReference reference)
    {
        SetKey(reference.Key);
        SetDomain(reference.Domain);
        SetVersion(reference.Version);
    }
}