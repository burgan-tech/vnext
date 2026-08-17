using System.Collections.Generic;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Scripting.Helpers;

/// <summary>
/// A single helper component source to be compiled into a helper set.
/// </summary>
/// <param name="Key">Component key (used for cache identity).</param>
/// <param name="Version">Component version (used for cache identity).</param>
/// <param name="Code">Decoded C# source of the helper.</param>
/// <param name="Path">A logical path/name used in diagnostics.</param>
public sealed record HelperSource(string Key, string Version, string Code, string Path);

/// <summary>
/// The compiled, loadable result of a helper set.
/// </summary>
/// <param name="Reference">Metadata reference to the compiled helper assembly.</param>
/// <param name="Namespaces">Public namespaces exported by the helper assembly (auto-imported into mappings).</param>
/// <param name="LoadContext">The collectible context the helper assembly is loaded into; the consuming
/// mapping must be compiled into the same context so helper types resolve at runtime.</param>
/// <param name="FromCache">True when this set was served from cache rather than freshly compiled.</param>
public sealed record HelperSet(
    MetadataReference Reference,
    IReadOnlyList<string> Namespaces,
    AssemblyLoadContext LoadContext,
    bool FromCache);

/// <summary>
/// Compiles and caches helper sets. A helper set is the expensive, process-wide artifact: it is
/// compiled once per content hash (ordered key+version+code plus the per-mapping grant) into a shared
/// collectible <see cref="AssemblyLoadContext"/>, then reused across requests. Implementations are
/// registered as singletons.
/// </summary>
public interface IScriptHelperRegistry
{
    /// <summary>
    /// Builds (or returns the cached) compiled helper set for the referenced helpers. Compiling them
    /// together lets helpers reference one another. The grant is part of the cache key.
    /// </summary>
    /// <param name="helpers">Ordered helper sources to compile together.</param>
    /// <param name="allowedAssemblies">Per-mapping sandbox grant merged on top of the baseline.</param>
    /// <param name="contractReferences">Runtime-owned contract references (e.g. ScriptBase) to include.</param>
    /// <param name="baseUsings">Using directives prepended to every helper source.</param>
    /// <param name="cancellationToken">Checked before the build starts so an abandoned caller does not
    /// kick off an expensive shared compile. It does NOT cancel a build in progress: the helper set is a
    /// process-wide artifact, so one caller going away must not fail the build other callers await.</param>
    HelperSet GetOrBuildHelpers(
        IReadOnlyList<HelperSource> helpers,
        IReadOnlyList<string>? allowedAssemblies,
        IEnumerable<MetadataReference> contractReferences,
        IEnumerable<string> baseUsings,
        CancellationToken cancellationToken = default);
}
