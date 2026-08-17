using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Scripting.Helpers;

/// <summary>
/// Default <see cref="IScriptHelperRegistry"/>. Each distinct (helpers + grant) content hash gets its
/// own collectible <see cref="AssemblyLoadContext"/> into which the helper assembly — and later the
/// consuming mapping — are loaded, so a superseded set can be unloaded independently. Operator-mounted
/// plugin DLLs are preloaded into each set's context so helper code can resolve them at runtime even
/// though the host does not reference them.
///
/// A failed build is never cached: <see cref="Lazy{T}"/> retains the factory's exception, and since this
/// registry is a singleton a cached fault would be replayed for the whole process lifetime, so faulted
/// entries are evicted and their load context unloaded.
/// </summary>
public sealed class ScriptHelperRegistry(IEvaluator evaluator, ScriptSandboxOptions? sandboxOptions = null)
    : IScriptHelperRegistry, IDisposable
{
    private readonly ScriptSandboxOptions _sandbox = sandboxOptions ?? new ScriptSandboxOptions { Enabled = false };
    private readonly ConcurrentDictionary<string, Lazy<HelperSet>> _cache = new();

    /// <inheritdoc />
    public HelperSet GetOrBuildHelpers(
        IReadOnlyList<HelperSource> helpers,
        IReadOnlyList<string>? allowedAssemblies,
        IEnumerable<MetadataReference> contractReferences,
        IEnumerable<string> baseUsings,
        CancellationToken cancellationToken = default)
    {
        if (helpers is null || helpers.Count == 0)
            throw new ArgumentException("At least one helper is required.", nameof(helpers));

        // The caller's token only gates entry: an already-abandoned request must not start an expensive
        // shared compile. Once the build starts it is deliberately detached from the request — see
        // BuildHelperSet.
        cancellationToken.ThrowIfCancellationRequested();

        var key = HashOf(helpers, allowedAssemblies);

        // Fast path: an already-materialised set is a cache hit no matter which caller produced it.
        if (_cache.TryGetValue(key, out var existing) && existing.IsValueCreated)
            return existing.Value with { FromCache = true };

        // Set from inside the factory so a call that triggered a real compile reports FromCache: false,
        // while calls served by an already-published set report a hit.
        var built = false;

        var lazy = _cache.GetOrAdd(key, _ => new Lazy<HelperSet>(
            () =>
            {
                built = true;
                return BuildHelperSet(key, helpers, allowedAssemblies, contractReferences, baseUsings);
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var set = lazy.Value;
            return built ? set : set with { FromCache = true };
        }
        catch
        {
            // Lazy<T> caches the *exception* as well as the value, so without eviction a single
            // transient failure would be replayed from cache for the rest of the process lifetime
            // (this registry is a singleton). Drop the poisoned entry so the next caller recompiles.
            Evict(key, lazy);
            throw;
        }
    }

    /// <summary>
    /// Compiles a helper set into its own collectible context. Compilation runs with
    /// <see cref="CancellationToken.None"/> on purpose: the result is a process-wide artifact shared by
    /// every request, so a single client disconnect (or a request timeout) must not be able to abort —
    /// and thereby fail — a build that all other callers are waiting on. On failure the context is
    /// unloaded so a failed build leaves nothing behind.
    /// </summary>
    private HelperSet BuildHelperSet(
        string key,
        IReadOnlyList<HelperSource> helpers,
        IReadOnlyList<string>? allowedAssemblies,
        IEnumerable<MetadataReference> contractReferences,
        IEnumerable<string> baseUsings)
    {
        var alc = new ScriptAssemblyLoadContext($"Helpers_{key[..8]}");

        try
        {
            PreloadPlugins(alc);

            var compiled = evaluator.CompileHelpers(
                sources: helpers.Select(h => (h.Path, h.Code)).ToList(),
                loadContext: alc,
                extraReferences: contractReferences,
                usingDirectives: baseUsings,
                sandboxGrant: allowedAssemblies,
                cancellationToken: CancellationToken.None);

            return new HelperSet(compiled.Reference, compiled.Namespaces, alc, FromCache: false, Key: key);
        }
        catch
        {
            // A failed build must not leak its collectible context and preloaded plugin assemblies.
            TryUnload(alc);
            throw;
        }
    }

    /// <summary>
    /// Removes a faulted cache entry, but only when it is still the entry we observed — never clobbers a
    /// healthy set that another caller has already published under the same key.
    ///
    /// This is the ONLY eviction path, and it only ever removes a faulted build. The evaluator's type
    /// cache depends on that: it keys compiled mappings by <see cref="HelperSet.Key"/>, a content hash,
    /// so evicting a healthy set would leave cached types pointing at an unloaded context. Any new
    /// eviction policy must invalidate the evaluator cache too.
    /// </summary>
    private void Evict(string key, Lazy<HelperSet> lazy)
    {
        _cache.TryRemove(new KeyValuePair<string, Lazy<HelperSet>>(key, lazy));
    }

    /// <summary>
    /// Loads operator-approved third-party DLLs from the plugin directory into the set's context so
    /// compiled helpers resolve them at runtime. Failures are ignored per-DLL (best effort).
    /// </summary>
    private void PreloadPlugins(AssemblyLoadContext context)
    {
        var dir = _sandbox.ResolvePluginDirectory();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
        {
            try
            {
                context.LoadFromAssemblyPath(dll);
            }
            catch
            {
                // A bad/duplicate plugin DLL must not break helper compilation.
            }
        }
    }

    private static string HashOf(IReadOnlyList<HelperSource> helpers, IReadOnlyList<string>? allowedAssemblies)
    {
        var sb = new StringBuilder();
        foreach (var h in helpers)
            sb.Append(h.Key).Append('@').Append(h.Version).Append('|').Append(h.Code).Append(';');

        if (allowedAssemblies is not null)
            foreach (var a in allowedAssemblies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append("@@").Append(a);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// Unloads a collectible context, tolerating unload failures (nothing actionable remains at that point).
    /// </summary>
    private static void TryUnload(AssemblyLoadContext context)
    {
        if (context is not ScriptAssemblyLoadContext owned)
            return;

        try
        {
            owned.Unload();
        }
        catch
        {
            // Ignore unload failures.
        }
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values.ToList())
        {
            // A faulted Lazy still reports IsValueCreated == false; reading .Value would rethrow.
            if (!entry.IsValueCreated)
                continue;

            TryUnload(entry.Value.LoadContext);
        }

        _cache.Clear();
    }
}
