using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

public sealed class SchemaDefinition : IDomainEntity, ISchemaReference, IReferenceSetter
{
    private SchemaDefinition()
    {
        Flow = RuntimeSysSchemaInfo.Schemas;
    }

    [JsonConstructor]
    private SchemaDefinition(
        string type,
        JsonElement schema
    ) : this()
    {
        Type = type;
        Schema = schema;
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; init; }

    /// <summary>
    /// This is information about the domain on which the stream where the record is located.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    public string Type { get; private set; }

    /// <summary>
    /// Schema Definition
    /// </summary>
    public JsonElement Schema { get; private set; }

    public static string ComponentTypeKey => RuntimeSysSchemaInfo.Schemas;
    public string ComponentKey => ComponentTypeKey;

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), SchemaConstants.MaxKeyLength);
    }

    private void SetDomain(string domain)
    {
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
    }

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    public void SetReference(IReference reference)
    {
        SetKey(reference.Key);
        SetDomain(reference.Domain);
        SetVersion(reference.Version);
    }
}