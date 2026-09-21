namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Rebuilds a task result's <c>Metadata</c>/<c>Headers</c> dictionary with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, so <b>neither</b> the routing mode nor a Dapr
/// round trip can change how a key looks up.
/// <para>
/// The two paths produce genuinely different dictionaries and always will. The shared invocation
/// cores build metadata with PascalCase keys (<c>"ExceptionType"</c>) under the default
/// <see cref="StringComparer.Ordinal"/> comparer. On the <b>local</b> path the consumer receives
/// that exact in-memory instance. On the <b>remote</b> path the same dictionary is JSON-serialized
/// by the Execution host and deserialized here by <c>System.Text.Json</c> with
/// <c>JsonSerializerDefaults.Web</c>, which camelCases string dictionary keys — so the consumer
/// receives <c>"exceptionType"</c> instead.
/// </para>
/// <para>
/// Normalizing only one side closes only half the hole, and this was shipped half-closed once:
/// fixing the remote side alone made <c>ErrorNormalizer</c>'s ordinal
/// <c>TryGetValue("ExceptionType", …)</c> succeed in both modes — which is what restored
/// <c>errorTypes</c> error-boundary matching — while leaving the <b>opposite</b> asymmetry in
/// place. A consumer that reads <c>metadata["exceptionType"]</c> (the camelCase spelling a domain
/// author observes from a remotely-routed task or from the journal) resolved on Remote and missed
/// on Local, and Local is now the shipped default. Applying this at both seams is what makes the
/// invariant hold in the form it was always stated: <b>the invocation mode is not observable
/// through a metadata or header lookup.</b>
/// </para>
/// <para>
/// This normalizes how keys COMPARE, never which keys exist: a null source stays null rather than
/// being promoted to an empty dictionary, so "metadata absent" and "metadata empty" stay distinct.
/// The stored spelling is deliberately left alone — the journal keeps whatever the producing path
/// wrote, and no consumer needs to care once lookups are case-insensitive on both sides.
/// </para>
/// </summary>
internal static class InvocationKeyNormalizer
{
    /// <inheritdoc cref="InvocationKeyNormalizer"/>
    public static Dictionary<string, TValue>? Normalize<TValue>(Dictionary<string, TValue>? source) =>
        source is null || ReferenceEquals(source.Comparer, StringComparer.OrdinalIgnoreCase)
            ? source
            : new Dictionary<string, TValue>(source, StringComparer.OrdinalIgnoreCase);
}
