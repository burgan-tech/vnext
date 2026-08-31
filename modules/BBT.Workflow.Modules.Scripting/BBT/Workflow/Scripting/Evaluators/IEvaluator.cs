using System;
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
/// The result of a compile-or-fetch call: the instantiated script plus whether THIS call performed
/// the actual Roslyn compile (<see cref="Compiled"/> = cache miss) and how long that compile took.
/// Exactly one caller per cache key observes <c>Compiled == true</c>.
/// </summary>
/// <typeparam name="T">The compiled instance type.</typeparam>
/// <param name="Instance">The compiled and service-injected instance.</param>
/// <param name="Compiled">True only for the single call whose factory ran the Roslyn emit.</param>
/// <param name="CompileDuration">Wall time of the Roslyn emit; <see cref="TimeSpan.Zero"/> on hits.</param>
/// <param name="Waited">
/// True when this call neither hit a completed cache entry nor compiled itself — it awaited (or
/// raced) another caller's in-flight compile. Telemetry needs the distinction: a waiter's wall time
/// is someone else's compile, and labelling it a plain hit made "hit" latency look like multi-second
/// compile time whenever a cold burst was in progress.
/// </param>
public sealed record EvaluatorCompilation<T>(T Instance, bool Compiled, TimeSpan CompileDuration, bool Waited = false);

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
    /// <param name="precomputedCacheKey">
    /// Optional cache key computed ahead of time via <see cref="BuildProfile"/> +
    /// <see cref="ComputeCacheKey"/>, bypassing this call's own key derivation. The caller MUST have
    /// produced it via BuildProfile+ComputeCacheKey with the very same inputs; a divergent key serves
    /// the wrong compiled type.
    /// </param>
    /// <returns>
    /// A task containing the compile outcome: the compiled instance of type T, whether this call
    /// performed the Roslyn compile (cache miss) or hit an already-compiled entry, and the compile
    /// duration (zero on a hit).
    /// </returns>
    Task<EvaluatorCompilation<T>> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default,
        AssemblyLoadContext? loadContext = null,
        IReadOnlyList<string>? sandboxGrant = null,
        string? precomputedCacheKey = null);

    /// <summary>
    /// Gets the number of cached compiled script types (unique compilation identities).
    /// </summary>
    int CachedTypeCount { get; }

    /// <summary>
    /// Builds the profile half of the cache key: everything EXCEPT the source and target type —
    /// sandbox flag, load-context scope, sorted grant, sorted usings, sorted reference displays.
    /// Deterministic and order-insensitive so callers may compute it once per stable input set
    /// (helper set / grant profile) and reuse it across compiles via <see cref="ComputeCacheKey"/>
    /// and <see cref="CompileToInstanceAsync{T}"/>'s <c>precomputedCacheKey</c> parameter.
    /// </summary>
    /// <param name="extraReferences">The extra metadata references that will be passed to compile.</param>
    /// <param name="usingDirectives">The using directives that will be passed to compile.</param>
    /// <param name="sandboxGrant">The sandbox grant that will be passed to compile.</param>
    /// <param name="loadContext">
    /// The load context that will be passed to compile. The cache scope is derived from this
    /// instance ITSELF (same disagree-proof rule as compiling): pass the very same context object
    /// you will pass to <see cref="CompileToInstanceAsync{T}"/> — a profile built against a
    /// different context yields a key for the wrong compilation identity.
    /// </param>
    /// <returns>The profile string.</returns>
    string BuildProfile(
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant,
        AssemblyLoadContext? loadContext);

    /// <summary>
    /// Combines a source hash (SHA-256 hex of the exact source text), the target type and a
    /// <see cref="BuildProfile"/> result into the final cache key. THE single key algorithm:
    /// the raw-string path routes through here too, so a precomputed key can never diverge.
    /// </summary>
    /// <param name="sourceHashHex">SHA-256 hex of the exact source text to be compiled.</param>
    /// <param name="targetType">The target type the compiled instance will be cast to.</param>
    /// <param name="profile">A <see cref="BuildProfile"/> result for the same compile inputs.</param>
    /// <returns>The cache key.</returns>
    string ComputeCacheKey(string sourceHashHex, Type targetType, string profile);

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
