using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Canonicalization;

namespace BBT.Workflow.RepresentationEtag;

/// <summary>
/// Generates representation ETags from the response body only: canonical JSON of the DTO then SHA256 + base64url.
/// Uses <see cref="JsonSerializerConstants.JsonOptions"/> and <see cref="IJsonCanonicalizer"/> from DI for consistency.
/// </summary>
public sealed class RepresentationEtagService(IJsonCanonicalizer canonicalizer) : IRepresentationEtagService
{
    /// <inheritdoc />
    public string Generate(object responseDto)
    {
        var json = JsonSerializer.Serialize(responseDto, JsonSerializerConstants.JsonOptions);
        var canonicalJson = canonicalizer.Canonicalize(json);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return ToBase64Url(hashBytes);
    }

    private static string ToBase64Url(byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
