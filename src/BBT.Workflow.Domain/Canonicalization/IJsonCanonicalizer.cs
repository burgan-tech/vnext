using System.Buffers;

namespace BBT.Workflow.Canonicalization;

/// <summary>
/// Canonicalizes JSON for deterministic hashing (e.g. representation ETag): object members sorted by
/// ordinal name, fixed number/string formatting, no whitespace. Stateless; registered as a singleton.
/// </summary>
public interface IJsonCanonicalizer
{
    /// <summary>
    /// Produces a canonical JSON string from the given JSON (sorted keys, consistent number/string formatting).
    /// Convenience wrapper over <see cref="Canonicalize(ReadOnlyMemory{byte}, IBufferWriter{byte})"/>.
    /// </summary>
    /// <param name="json">Raw JSON string.</param>
    /// <returns>Canonical JSON string.</returns>
    string Canonicalize(string json);

    /// <summary>
    /// Writes the canonical UTF-8 form of <paramref name="utf8Json"/> to <paramref name="output"/> in a single
    /// pass, without materializing intermediate strings. This is the hot-path API used for ETag hashing.
    /// </summary>
    /// <param name="utf8Json">UTF-8 encoded JSON document (any top-level value).</param>
    /// <param name="output">Destination for the canonical UTF-8 bytes.</param>
    void Canonicalize(ReadOnlyMemory<byte> utf8Json, IBufferWriter<byte> output);
}
