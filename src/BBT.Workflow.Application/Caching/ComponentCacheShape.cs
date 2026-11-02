namespace BBT.Workflow.Caching;

/// <summary>
/// The shape version of the component entities (workflow, task, schema, function, view, extension, mapping)
/// that <see cref="CacheSet{T}"/> serializes into the shared cache. It is a segment of every full and
/// resolution key, so runtimes whose entity types differ never read each other's entries.
/// </summary>
/// <remarks>
/// <para>
/// The cache is shared by every pod. During a rolling upgrade or a rollback, pods of two releases run side by
/// side; a pod whose entity type lacks a member re-writes an entry without it (System.Text.Json drops unknown
/// members), and a newer pod reading that entry sees the member as absent. When absence is unsafe — the
/// motivating case is <c>Workflow.RowSecurity</c>, whose absence means "no row filter" — the newer pod would
/// fail open. Keying entries by shape makes the two releases use disjoint keys instead.
/// </para>
/// <para>
/// Bump this value when a cached component type gains a member whose absence is unsafe (a security filter, an
/// authorization rule, anything that turns a missing value into a wider answer). Purely additive members whose
/// absence is harmless need no bump. A bump costs one cold load per component per pod; the old entries expire
/// on their own TTL. Generation tokens (<c>:gen</c>) are opaque and stay shared across shapes.
/// </para>
/// <para>History: no segment — before row security; <c>s2</c> — <c>Workflow.RowSecurity</c> (row security, vnext#1107).</para>
/// </remarks>
public static class ComponentCacheShape
{
    /// <summary>The current shape segment, written between the component key and the entry kind.</summary>
    public const string Version = "s2";
}
