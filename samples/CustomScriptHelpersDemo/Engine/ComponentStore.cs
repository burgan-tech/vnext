using System.Text.Json;

namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// Loads script components from a folder layout that mimics the vNext component store:
///   components/helpers/&lt;key&gt;.csx   — reusable helper classes
///   components/mappings/&lt;name&gt;.csx — transition mapping scripts
///   components/flows/&lt;name&gt;.json   — flow definitions
/// </summary>
public sealed class ComponentStore(string root)
{
    private readonly Dictionary<string, ScriptComponent> _helpers = LoadKeyed(Path.Combine(root, "helpers"));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Resolves a helper component by key (e.g. "tax-calculator").</summary>
    public ScriptComponent Helper(string key) =>
        _helpers.TryGetValue(key, out var c)
            ? c
            : throw new KeyNotFoundException($"Helper component '{key}' not found in store.");

    /// <summary>Loads any component by its store-relative path (e.g. "mappings/order-mapping.csx").</summary>
    public ScriptComponent Load(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        return new ScriptComponent(Path.GetFileNameWithoutExtension(path), path, File.ReadAllText(path));
    }

    /// <summary>Loads a flow definition by its store-relative path.</summary>
    public FlowDefinition LoadFlow(string relativePath) =>
        JsonSerializer.Deserialize<FlowDefinition>(File.ReadAllText(Path.Combine(root, relativePath)), JsonOptions)
        ?? throw new InvalidOperationException($"Flow '{relativePath}' could not be parsed.");

    private static Dictionary<string, ScriptComponent> LoadKeyed(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.csx").ToDictionary(
                p => Path.GetFileNameWithoutExtension(p)!,
                p => new ScriptComponent(Path.GetFileNameWithoutExtension(p), p, File.ReadAllText(p)))
            : new();
}
