namespace BBT.Workflow.RepresentationEtag;

/// <summary>
/// Generates representation ETags for HTTP cache validation from the response body only.
/// Representation ETag = base64url(SHA256(canonical JSON of the response DTO)).
/// </summary>
public interface IRepresentationEtagService
{
    /// <summary>
    /// Generates a representation ETag by serializing the response DTO to canonical JSON and hashing it.
    /// The DTO's Etag property must be unset (null/empty) when passed so the hash is deterministic.
    /// </summary>
    /// <param name="responseDto">The response DTO that will be returned (EntityEtag may be set; Etag must not be set yet).</param>
    /// <returns>Unquoted representation ETag string (base64url SHA256 of canonical JSON).</returns>
    string Generate(object responseDto);
}
