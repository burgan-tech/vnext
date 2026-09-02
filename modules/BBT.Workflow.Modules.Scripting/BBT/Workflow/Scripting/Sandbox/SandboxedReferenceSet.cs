using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Builds the curated <see cref="MetadataReference"/> list for sandboxed compilation.
/// The universe of resolvable assemblies is the runtime's Trusted Platform Assemblies (TPA)
/// plus any DLLs in the operator-mounted plugin directory. From that universe only the effective
/// allow-list (global baseline ∪ per-mapping grant) is kept.
/// <para>
/// Everything expensive is cached process-wide: the TPA map (immutable for the process lifetime),
/// the plugin-directory listing (operator-mounted at deploy time — a changed directory needs a
/// restart, same rule as the plugin DLLs themselves), and each <see cref="MetadataReference"/>
/// (a CreateFromFile re-opens and re-decodes the PE metadata; before this cache every single
/// compile in the sandboxed host paid filesystem I/O plus ~20 metadata decodes).
/// </para>
/// </summary>
public static class SandboxedReferenceSet
{
    /// <summary>Path → decoded reference. A reference is immutable and safely shareable.</summary>
    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simple-name → path map of the TPA list; fixed for the process lifetime.</summary>
    private static readonly Lazy<Dictionary<string, string>> TpaMap = new(
        BuildTpaMap, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One listing per plugin directory, taken on first use.</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> PluginMapsByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Effective allow-list = global baseline ∪ the per-mapping grant. Only assemblies that are
    /// actually available (framework TPA or mounted plugins) can resolve; banned-namespace usage
    /// is still rejected by <see cref="BannedApiAnalyzer"/> regardless of the grant.
    /// </summary>
    public static IReadOnlyList<MetadataReference> Build(ScriptSandboxOptions options, IEnumerable<string>? grant = null)
    {
        var allowed = new HashSet<string>(options.AllowedAssemblies, StringComparer.OrdinalIgnoreCase);
        if (grant is not null)
            allowed.UnionWith(grant);

        var tpa = TpaMap.Value;
        var plugins = PluginMap(options.ResolvePluginDirectory());

        var refs = new List<MetadataReference>(allowed.Count);
        foreach (var name in allowed)
        {
            // Framework assemblies always win over a same-named plugin.
            if (tpa.TryGetValue(name, out var path) || plugins.TryGetValue(name, out path))
                refs.Add(ReferenceByPath.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p)));
        }

        return refs;
    }

    private static Dictionary<string, string> BuildTpaMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in tpa)
            map[Path.GetFileNameWithoutExtension(path)] = path;

        return map;
    }

    /// <summary>Dynamically-loaded third-party DLLs from the plugin directory, listed once.</summary>
    private static Dictionary<string, string> PluginMap(string pluginDirectory)
    {
        if (string.IsNullOrEmpty(pluginDirectory))
            return [];

        return PluginMapsByDirectory.GetOrAdd(pluginDirectory, static dir =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(dir))
                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                    map.TryAdd(Path.GetFileNameWithoutExtension(dll), dll);

            return map;
        });
    }
}
