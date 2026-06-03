using System.Collections.Concurrent;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using CustomScriptHelpersDemo.Contracts;
using Microsoft.CodeAnalysis;

namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// Compiles helper components and mapping scripts on demand. The contract is:
/// <c>build the referenced helper set first (cached by content hash), then compile
/// the mapping against it and run</c>. Helpers and mappings share one collectible
/// AssemblyLoadContext so the mapping resolves helper types at runtime.
/// </summary>
public sealed class ScriptComponentEngine
{
    private readonly ScriptCompiler _compiler;
    private readonly MetadataReference _contractRef;
    private readonly AssemblyLoadContext _alc = new("ScriptComponents", isCollectible: true);

    // Helper sets are the expensive, shared artifact — cache them by content hash.
    private readonly ConcurrentDictionary<string, CompiledAssembly> _helperCache = new();

    private static readonly string[] BaseUsings =
    {
        "System",
        "System.Collections.Generic",
        "System.Threading.Tasks",
        "CustomScriptHelpersDemo.Contracts",
    };

    public ScriptComponentEngine(ScriptCompiler compiler)
    {
        _compiler = compiler;
        _contractRef = MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location);
    }

    /// <summary>
    /// Builds (or returns cached) the combined assembly for a set of helper components.
    /// Compiling them together lets helpers reference one another.
    /// </summary>
    public (CompiledAssembly Helpers, bool FromCache) GetOrBuildHelpers(
        IReadOnlyList<ScriptComponent> helpers,
        IReadOnlyList<string>? allowedAssemblies = null)
    {
        // The allow-list is part of the compilation identity — fold it into the cache key.
        var key = HashOf(helpers, allowedAssemblies);
        var fromCache = _helperCache.ContainsKey(key);
        var compiled = _helperCache.GetOrAdd(key, _ =>
            _compiler.Compile(
                assemblyName: $"Helpers_{key[..8]}",
                sources: helpers.Select(h => (h.Path, h.Code)).ToList(),
                loadContext: _alc,
                extraAllowedAssemblies: allowedAssemblies));
        return (compiled, fromCache);
    }

    /// <summary>
    /// Compiles a mapping component against the supplied helper set (references + auto-usings)
    /// and returns a ready-to-run instance with services injected.
    /// </summary>
    public IMapping BuildMapping(
        ScriptComponent mapping,
        CompiledAssembly? helpers,
        IScriptServices services,
        IReadOnlyList<string>? allowedAssemblies = null)
    {
        var references = new List<MetadataReference> { _contractRef };
        var usings = BaseUsings.AsEnumerable();

        if (helpers is not null)
        {
            references.Add(helpers.Reference);
            usings = usings.Concat(helpers.Namespaces); // auto-import helper namespaces
        }

        var compiled = _compiler.Compile(
            assemblyName: $"Mapping_{HashOf([mapping], allowedAssemblies)[..8]}",
            sources: [(mapping.Path, mapping.Code)],
            loadContext: _alc,
            extraReferences: references,
            usingDirectives: usings,
            extraAllowedAssemblies: allowedAssemblies);

        var type = compiled.Assembly.GetTypes()
            .First(t => typeof(IMapping).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

        var instance = (IMapping)Activator.CreateInstance(type)!;
        ((ScriptBase)instance).SetServices(services);
        return instance;
    }

    private static string HashOf(IReadOnlyList<ScriptComponent> components, IReadOnlyList<string>? allowedAssemblies = null)
    {
        var sb = new StringBuilder();
        foreach (var c in components)
            sb.Append(c.Key).Append('|').Append(c.Code).Append(';');
        if (allowedAssemblies is not null)
            foreach (var a in allowedAssemblies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append("@@").Append(a);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
