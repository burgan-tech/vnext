using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Builds the curated <see cref="MetadataReference"/> list for sandboxed compilation.
/// The universe of resolvable assemblies is the runtime's Trusted Platform Assemblies (TPA)
/// plus any DLLs in the operator-mounted plugin directory. From that universe only the effective
/// allow-list (global baseline ∪ per-mapping grant) is kept.
/// </summary>
public static class SandboxedReferenceSet
{
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

        var available = AvailableAssemblies(options.ResolvePluginDirectory());

        var refs = new List<MetadataReference>();
        foreach (var name in allowed)
            if (available.TryGetValue(name, out var path))
                refs.Add(MetadataReference.CreateFromFile(path));

        return refs;
    }

    /// <summary>Simple-name → path map for framework (TPA) + plugin-directory assemblies.</summary>
    private static Dictionary<string, string> AvailableAssemblies(string pluginDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in tpa)
            map[Path.GetFileNameWithoutExtension(path)] = path;

        // Dynamically-loaded third-party DLLs from the plugin directory.
        // TryAdd so framework assemblies always win over a same-named plugin.
        if (!string.IsNullOrEmpty(pluginDirectory) && Directory.Exists(pluginDirectory))
            foreach (var dll in Directory.EnumerateFiles(pluginDirectory, "*.dll"))
                map.TryAdd(Path.GetFileNameWithoutExtension(dll), dll);

        return map;
    }
}
