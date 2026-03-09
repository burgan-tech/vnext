namespace BBT.Workflow.Canonicalization;

/// <summary>
/// Canonicalizes JSON for deterministic hashing (e.g. representation ETag).
/// Registered as scoped; one instance per request, buffer reused within the scope.
/// </summary>
public interface IJsonCanonicalizer
{
    /// <summary>
    /// Produces a canonical JSON string from the given JSON (sorted keys, consistent number/string formatting).
    /// </summary>
    /// <param name="json">Raw JSON string.</param>
    /// <returns>Canonical JSON string.</returns>
    string Canonicalize(string json);
}
