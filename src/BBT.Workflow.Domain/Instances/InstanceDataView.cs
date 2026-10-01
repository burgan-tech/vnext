namespace BBT.Workflow.Instances;

/// <summary>
/// The plaintext view of one stored <see cref="InstanceData"/> row, as the engine sees it.
/// <para>
/// For a row without <c>x-encryption.type: "encrypt"</c> tokens the view IS the stored document
/// (<see cref="Plain"/> is the same <see cref="JsonData"/> instance). For a row carrying tokens,
/// <see cref="Plain"/> holds the decrypted document, <see cref="Tokens"/> the stored token of every
/// decrypted path (the read path serves it to callers outside the exemption list, and the write
/// funnel carries it forward unchanged), and <see cref="Undecryptable"/> the paths whose token could
/// not be opened — an unknown key id or a failed authentication. Those paths keep the token string in
/// <see cref="Plain"/>; the engine must never act on them (see <c>Instance:100040</c>).
/// </para>
/// </summary>
public sealed class InstanceDataView
{
    private static readonly IReadOnlyDictionary<string, string> NoTokens =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Creates a view.</summary>
    public InstanceDataView(
        JsonData plain,
        IReadOnlyDictionary<string, string>? tokens = null,
        IReadOnlySet<string>? undecryptable = null)
    {
        Plain = plain;
        Tokens = tokens ?? NoTokens;
        Undecryptable = undecryptable ?? NoPaths;
    }

    /// <summary>The document with every decryptable token replaced by its plaintext.</summary>
    public JsonData Plain { get; }

    /// <summary>Stored token per dot path, for every token that was decrypted.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; }

    /// <summary>Dot paths whose token could not be decrypted (their value in <see cref="Plain"/> is the token).</summary>
    public IReadOnlySet<string> Undecryptable { get; }

    /// <summary>True when the stored row carries at least one token.</summary>
    public bool HasTokens => Tokens.Count > 0 || Undecryptable.Count > 0;

    /// <summary>A view over a document that holds no tokens.</summary>
    public static InstanceDataView Of(JsonData plain) => new(plain);
}
