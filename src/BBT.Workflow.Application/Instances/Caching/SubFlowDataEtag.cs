namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// The data-function ETag of an instance with an active <c>S</c> SubFlow:
/// <c>{parentEtag}.{childEtag}</c>, where the child part is the child's own data-function ETag.
/// <para>
/// While a SubFlow is open the parent's own data and state do not move, but the <c>extensions</c>
/// block the parent serves is read from the child. The child's data ETag is the change signal of that
/// output (its data and its state — the same "source is data" rule the parent applies to itself), so it
/// rides in the parent's ETag. Carrying it verbatim (not hashed) lets the parent forward it to the
/// child as <c>If-None-Match</c> and answer 304 from the child's own fingerprint fast path, without
/// running any extension. A child that itself waits on a SubFlow returns a composed ETag in turn, so
/// the chain is <c>p.c.g</c> and each level splits at its first separator.
/// </para>
/// </summary>
public static class SubFlowDataEtag
{
    /// <summary>
    /// Separator between the parent part and the child part. The parent part is a lowercase hex
    /// digest and never contains it.
    /// </summary>
    public const char Separator = '.';

    /// <summary>Composes the ETag served for an instance with an active SubFlow.</summary>
    public static string Compose(string parentEtag, string childEtag) =>
        string.Concat(parentEtag, Separator.ToString(), childEtag.StripQuotes());

    /// <summary>
    /// Returns the child part of a presented <c>If-None-Match</c> when its parent part equals
    /// <paramref name="parentEtag"/>; otherwise null (no conditional request is sent to the child).
    /// </summary>
    public static string? ChildPartFor(string? ifNoneMatch, string parentEtag)
    {
        var presented = ifNoneMatch.StripQuotes();
        var separator = presented.IndexOf(Separator);
        if (separator <= 0 || separator == presented.Length - 1)
            return null;

        return string.Equals(presented[..separator], parentEtag, StringComparison.Ordinal)
            ? presented[(separator + 1)..]
            : null;
    }
}
