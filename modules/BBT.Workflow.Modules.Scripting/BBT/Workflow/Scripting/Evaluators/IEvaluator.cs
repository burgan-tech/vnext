using System.Collections.Generic;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Functions;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// The compiled image of a helper set: a reference other scripts can be compiled against plus the
/// public namespaces it exposes (auto-imported into the consuming mapping).
/// </summary>
/// <param name="Reference">Metadata reference to the compiled helper assembly.</param>
/// <param name="Namespaces">Public namespaces exported by the helper assembly.</param>
public sealed record CompiledHelpers(MetadataReference Reference, string[] Namespaces);

/// <summary>
/// Provides script compilation and instantiation capabilities.
/// Implementations should cache compiled types for performance.
/// </summary>
public interface IEvaluator
{
    /// <summary>
    /// Compiles C# code into an instance of the specified type asynchronously.
    /// If the instance inherits from ScriptBase, the provided services will be injected.
    /// </summary>
    /// <typeparam name="T">The target type to compile the code into</typeparam>
    /// <param name="code">The C# code to compile</param>
    /// <param name="services">Optional script services to inject into ScriptBase instances</param>
    /// <param name="extraReferences">Optional additional metadata references for compilation</param>
    /// <param name="usingDirectives">Optional additional using directives to include</param>
    /// <param name="cancellationToken">
    /// Gates entry only: checked before a compile is looked up/started, but not honoured once one is
    /// under way. A compile for a given cache key is shared by every caller waiting on it, so one
    /// caller's token cannot cancel work the others are still waiting on.
    /// </param>
    /// <param name="loadContext">
    /// Optional shared collectible <see cref="AssemblyLoadContext"/> to load the compiled assembly into.
    /// Used so a mapping resolves the helper types compiled into the same context. When <c>null</c> a
    /// fresh per-script context is created (legacy behaviour).
    ///
    /// The context is also part of the cache-key identity: two callers compiling identical source into
    /// different shared contexts must never share a cached type. Implementations must derive that
    /// identity from this parameter itself rather than take it as a separate argument, so the two can
    /// never be supplied out of agreement.
    /// </param>
    /// <param name="sandboxGrant">
    /// Optional per-compile assembly grant merged on top of the sandbox baseline (effective only when
    /// the sandbox is enabled).
    /// </param>
    /// <returns>A task containing the compiled instance of type T</returns>
    Task<T> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default,
        AssemblyLoadContext? loadContext = null,
        IReadOnlyList<string>? sandboxGrant = null);

    /// <summary>
    /// Compiles a set of helper component sources into a single assembly loaded into the supplied
    /// collectible <see cref="AssemblyLoadContext"/>, so the helpers can reference one another and a
    /// consuming mapping can be compiled against the result. Runs the sandbox analyzer when enabled.
    /// </summary>
    /// <param name="sources">The helper sources (path + code) to compile together.</param>
    /// <param name="loadContext">The shared collectible context to load the helper assembly into.</param>
    /// <param name="extraReferences">Runtime-owned contract references (e.g. ScriptBase) to include.</param>
    /// <param name="usingDirectives">Optional using directives prepended to every helper source.</param>
    /// <param name="sandboxGrant">Optional per-compile assembly grant.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The compiled helper image (reference + exported namespaces).</returns>
    CompiledHelpers CompileHelpers(
        IReadOnlyList<(string Path, string Code)> sources,
        AssemblyLoadContext loadContext,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        IReadOnlyList<string>? sandboxGrant = null,
        CancellationToken cancellationToken = default);
}
