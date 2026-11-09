using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using BBT.Workflow.Canonicalization;

namespace BBT.Workflow.RepresentationEtag;

/// <summary>
/// Generates representation ETags from the response body only: canonical JSON of the DTO then SHA256 +
/// base64url. Serializes with <see cref="JsonSerializerConstants.JsonOptions"/> (shape, naming, ignore
/// rules) straight to UTF-8 in a pooled buffer, canonicalizes in one pass into a second pooled buffer
/// and hashes that span — no intermediate strings. Stateless; registered as a singleton.
/// </summary>
public sealed class RepresentationEtagService(IJsonCanonicalizer canonicalizer) : IRepresentationEtagService
{
    /// <summary>
    /// The intermediate JSON is only parsed again, never emitted, and the canonical form depends on the
    /// decoded string values alone. The relaxed encoder therefore cannot change the ETag; it only keeps
    /// non-ASCII / HTML-sensitive text unescaped so the canonicalizer can copy it verbatim instead of
    /// decoding <c>\uXXXX</c> escapes (pinned by the ETag parity tests against the old formula).
    /// </summary>
    private static readonly JsonWriterOptions IntermediateWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = JsonSerializerConstants.JsonOptions.MaxDepth
    };

    /// <inheritdoc />
    public string Generate(object responseDto)
    {
        using var json = new PooledByteBufferWriter(4096);
        using (var writer = new Utf8JsonWriter(json, IntermediateWriterOptions))
        {
            JsonSerializer.Serialize(writer, responseDto, JsonSerializerConstants.JsonOptions);
        }

        using var canonical = new PooledByteBufferWriter(json.WrittenCount);
        canonicalizer.Canonicalize(json.WrittenMemory, canonical);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(canonical.WrittenSpan, hash);
        return Base64Url.EncodeToString(hash);
    }
}
