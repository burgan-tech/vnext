namespace BBT.Workflow.Definitions;

/// <summary>
/// HTTP verbs a <see cref="Function"/> can declare support for.
/// A function that declares no verbs accepts every verb the runtime routes, preserving the
/// behaviour of definitions authored before verb declaration existed.
/// </summary>
public static class FunctionVerb
{
    /// <summary>
    /// Read without a request body.
    /// </summary>
    public const string Get = "GET";

    /// <summary>
    /// Create or invoke with a request body.
    /// </summary>
    public const string Post = "POST";

    /// <summary>
    /// Partial update with a request body.
    /// </summary>
    public const string Patch = "PATCH";

    /// <summary>
    /// Delete.
    /// </summary>
    public const string Delete = "DELETE";

    // The HTTP QUERY method (a safe, idempotent read carrying a request body) is deliberately not
    // supported yet: the surrounding tooling - Swagger/OpenAPI generation, gateways, client SDKs -
    // does not handle an unrecognised method. Revisit once that ecosystem catches up.

    /// <summary>
    /// Every verb the runtime recognises, in declaration order.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Get, Post, Patch, Delete];

    /// <summary>
    /// True when <paramref name="verb"/> is a recognised verb, ignoring case and surrounding space.
    /// </summary>
    public static bool IsKnown(string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            return false;

        var normalized = Normalize(verb);
        return All.Any(v => string.Equals(v, normalized, StringComparison.Ordinal));
    }

    /// <summary>
    /// Normalizes a verb for comparison: trimmed and upper-cased.
    /// </summary>
    public static string Normalize(string verb) => verb.Trim().ToUpperInvariant();

    /// <summary>
    /// True when the verb carries a request body the runtime can validate against an input schema.
    /// </summary>
    public static bool CarriesBody(string? verb) =>
        Normalize(verb ?? string.Empty) is Post or Patch or Delete;
}
