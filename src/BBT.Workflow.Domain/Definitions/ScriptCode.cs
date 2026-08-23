using System.Text.Json.Serialization;
using BBT.Aether.Domain.Values;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents a script code value object. The body may be plain text (<see cref="CodeEncoding.Native"/>),
/// Base64 (<see cref="CodeEncoding.Base64"/>), or a reference to a reusable <c>sys-mappings</c> component
/// (<see cref="CodeEncoding.Reference"/>, in which case <see cref="CodeReference"/> is populated and the
/// body is resolved from the component store at compile time).
/// Helper references and the per-compile sandbox grant live under <see cref="Scripts"/>.
/// Deserialized via <see cref="ScriptCodeJsonConverter"/> because the <c>code</c> field is polymorphic.
/// </summary>
public sealed class ScriptCode : ValueObject
{
    /// <summary>
    /// Default location value when none is provided.
    /// </summary>
    public const string DefaultLocation = "inline";

    /// <summary>
    /// The location/path identifier for the script.
    /// Defaults to "inline" when not specified.
    /// </summary>
    public string Location { get; private set; }

    /// <summary>
    /// The script code content (string) for <see cref="CodeEncoding.Native"/> / <see cref="CodeEncoding.Base64"/>.
    /// Empty when <see cref="Encoding"/> is <see cref="CodeEncoding.Reference"/> (see <see cref="CodeReference"/>).
    /// </summary>
    public string Code { get; private set; }

    /// <summary>
    /// Reference to a <c>sys-mappings</c> component supplying the body. Populated only when
    /// <see cref="Encoding"/> is <see cref="CodeEncoding.Reference"/>.
    /// </summary>
    public Reference? CodeReference { get; private set; }

    /// <summary>
    /// The mapping type for script code execution (Global or Local).
    /// </summary>
    public MappingType Type { get; private set; }

    /// <summary>
    /// The encoding of the code content: Base64 (default), Native (plain text), or Reference (sys-mappings).
    /// </summary>
    public CodeEncoding Encoding { get; private set; }

    /// <summary>
    /// Optional script settings (helper references + per-compile sandbox grant). Unioned with the
    /// flow-level <c>scripts</c> at compile time.
    /// </summary>
    public ScriptSettings? Scripts { get; private set; }

    private ScriptCode()
    {
        Location = DefaultLocation;
        Code = string.Empty;
        Type = MappingType.Local;
        Encoding = CodeEncoding.Base64;
    }

    /// <summary>
    /// Creates a new ScriptCode instance.
    /// </summary>
    /// <param name="location">The location/path identifier. Defaults to "inline" if null or empty.</param>
    /// <param name="code">The script code content (for Native/Base64). Ignored for Reference encoding.</param>
    /// <param name="type">The mapping type (Global or Local). Defaults to Local.</param>
    /// <param name="encoding">The code encoding. Defaults to Base64 for backward compatibility.</param>
    /// <param name="scripts">Optional script settings (helpers + sandbox grant).</param>
    /// <param name="codeReference">Reference to a sys-mappings component (only for Reference encoding).</param>
    public ScriptCode(
        string? location,
        string? code,
        MappingType? type = null,
        CodeEncoding? encoding = null,
        ScriptSettings? scripts = null,
        Reference? codeReference = null)
    {
        Location = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location;
        Code = code ?? string.Empty;
        Type = type ?? MappingType.Local;
        Encoding = encoding ?? CodeEncoding.Base64;
        Scripts = scripts;
        CodeReference = Encoding.Equals(CodeEncoding.Reference) ? codeReference : null;
    }

    /// <summary>
    /// True when this script references one or more helper components and therefore requires
    /// the sandboxed helper-set compile path.
    /// </summary>
    [JsonIgnore]
    public bool HasHelpers => Scripts?.HasHelpers == true;

    /// <summary>
    /// True when this script encoding is a reference to a sys-mappings component.
    /// </summary>
    [JsonIgnore]
    public bool IsReference => Encoding.Equals(CodeEncoding.Reference);

    /// <summary>
    /// True when there is an actual mapping body to compile and run. Used by executors to decide
    /// whether to invoke the script engine. For Reference encoding this is true when a
    /// <see cref="CodeReference"/> is set; otherwise when the inline <see cref="Code"/> is non-empty.
    /// Always false for the Global mapping type.
    /// </summary>
    [JsonIgnore]
    public bool HasMappingCode =>
        !Type.Equals(MappingType.Global)
        && (IsReference ? CodeReference is not null : !string.IsNullOrWhiteSpace(Code));

    private string? _decodedCode;

    /// <summary>
    /// Gets the decoded/usable inline script code content.
    /// For Base64 encoding, decodes the content. For Native encoding, returns the code as-is.
    /// Returns empty string for Global mapping type.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Base64 decoding fails, or when the encoding is Reference (the body must be resolved
    /// from the component store by the script engine, not read inline).
    /// </exception>
    public string DecodedCode => _decodedCode ??= ComputeDecodedCode();

    private string ComputeDecodedCode()
    {
        if (Type.Equals(MappingType.Global))
        {
            return string.Empty;
        }

        if (IsReference)
        {
            return string.Empty;
        }

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
            throw new InvalidOperationException("Invalid Base64 string in ScriptCode.", ex);
        }
    }

    private string? _contentHash;

    /// <summary>
    /// SHA-256 hex of <see cref="DecodedCode"/>, computed once per instance. Content-derived —
    /// safe as a cache-identity component regardless of how this instance was materialized
    /// (fresh deserialization per read included). Empty-source scripts hash the empty string.
    /// Benign race: concurrent first accesses may compute twice and publish the same value.
    /// </summary>
    [JsonIgnore]
    public string ContentHash => _contentHash ??=
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DecodedCode)));

    /// <summary>
    /// Creates a ScriptCode instance with native (plain text) encoding.
    /// </summary>
    public static ScriptCode FromNative(string code, string? location = null, MappingType? type = null)
    {
        return new ScriptCode(location, code, type, CodeEncoding.Native);
    }

    /// <summary>
    /// Creates a ScriptCode instance with Base64 encoded content.
    /// </summary>
    public static ScriptCode FromBase64(string base64Code, string? location = null, MappingType? type = null)
    {
        return new ScriptCode(location, base64Code, type, CodeEncoding.Base64);
    }

    /// <summary>
    /// Creates a ScriptCode instance that references a sys-mappings component for its body.
    /// </summary>
    public static ScriptCode FromReference(Reference codeReference, string? location = null, MappingType? type = null)
    {
        return new ScriptCode(location, code: null, type, CodeEncoding.Reference, scripts: null, codeReference);
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Location;
        yield return Code;
        yield return Type;
        yield return Encoding;

        if (CodeReference is not null)
        {
            yield return CodeReference.ToString();
        }

        if (Scripts is not null)
        {
            yield return Scripts;
        }
    }
}
