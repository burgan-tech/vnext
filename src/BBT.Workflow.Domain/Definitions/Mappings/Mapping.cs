using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// A mapping component (<c>sys-mappings</c>): a reusable, versioned C# script distributed with the
/// domain like flows/tasks/views. A mapping component is typically authored as a helper class body
/// (a <c>.csx</c> snippet) that transition mappings reference via <c>mapping.helpers[]</c>.
/// The referenced set is compiled (sandboxed, cached by content hash) before the mapping that uses it.
/// </summary>
public sealed class Mapping : IDomainEntity, IMappingReference, IReferenceSetter
{
    private Mapping()
    {
        Flow = RuntimeSysSchemaInfo.Mappings;
        Name = string.Empty;
        Code = string.Empty;
        Encoding = CodeEncoding.Base64;
    }

    [JsonConstructor]
    public Mapping(
        string name,
        string code,
        CodeEncoding? encoding = null) : this()
    {
        Name = name ?? string.Empty;
        Code = code ?? string.Empty;
        Encoding = encoding ?? CodeEncoding.Base64;
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
    /// Information about which domain the flow is working on and which domain it belongs to.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    /// <summary>
    /// Human-readable name of the mapping component.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// The script code content. Can be Base64 encoded or native (plain text) based on <see cref="Encoding"/>.
    /// </summary>
    public string Code { get; private set; }

    /// <summary>
    /// The encoding of <see cref="Code"/>. Base64 (default, backward compatible) or Native (plain text).
    /// </summary>
    public CodeEncoding Encoding { get; private set; }

    /// <summary>
    /// Semantic Version
    /// </summary>
    public string SemanticVersion => Regex.Match(Version, @"^([^+]+)").Groups[1].Value;

    /// <summary>
    /// The decoded, compilable C# source. For Native encoding, returns the code as-is;
    /// for Base64, decodes the content.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when Base64 decoding fails.</exception>
    public string DecodedCode
    {
        get
        {
            if (Encoding.Equals(CodeEncoding.Native))
            {
                return Code;
            }

            try
            {
                var bytes = Convert.FromBase64String(Code);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Invalid Base64 string in Mapping code.", ex);
            }
        }
    }

    public static string ComponentTypeKey => RuntimeSysSchemaInfo.Mappings;
    public string ComponentKey => ComponentTypeKey;

    public static string GenerateCacheKey(
        string domain,
        string flow,
        string key,
        string version)
    {
        return $"{nameof(Mapping)}:{domain}:{flow}:{key}:{version}";
    }

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), MappingConstants.MaxKeyLength);
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
