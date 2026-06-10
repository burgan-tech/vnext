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

        var key = HashOf(helpers, allowedAssemblies);
        var fromCache = _cache.ContainsKey(key);

        var lazy = _cache.GetOrAdd(key, _ => new Lazy<HelperSet>(() =>
        {
            var alc = new ScriptAssemblyLoadContext($"Helpers_{key[..8]}");
            PreloadPlugins(alc);

            var compiled = evaluator.CompileHelpers(
                sources: helpers.Select(h => (h.Path, h.Code)).ToList(),
                loadContext: alc,
                extraReferences: contractReferences,
                usingDirectives: baseUsings,
                sandboxGrant: allowedAssemblies,
                cancellationToken: cancellationToken);

            return new HelperSet(compiled.Reference, compiled.Namespaces, alc, FromCache: false);
        }));

        var set = lazy.Value;
        return fromCache ? set with { FromCache = true } : set;
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

    public void Dispose()
    {
        foreach (var entry in _cache.Values.ToList())
        {
            if (!entry.IsValueCreated)
                continue;

            if (entry.Value.LoadContext is ScriptAssemblyLoadContext owned)
            {
                try
                {
                    owned.Unload();
                }
                catch
                {
                    // Ignore unload failures.
                }
            }
        }

        _cache.Clear();
    }
}
