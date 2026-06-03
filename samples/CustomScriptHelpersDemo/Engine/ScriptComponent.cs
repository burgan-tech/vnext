using System.Text.Json.Serialization;

namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// A stored script component (helper or mapping), addressed by key — the same way
/// vNext stores flows/tasks/views. In the real runtime this comes from the component
/// cache store; here it is a .csx file on disk.
/// </summary>
public sealed record ScriptComponent(string Key, string Path, string Code);

// ----- Flow definition DTOs (subset) -------------------------------------------------

/// <summary>Minimal flow definition; only the mapping reference is modelled here.</summary>
public sealed record FlowDefinition(
    string Key,
    string Version,
    List<FlowTransition> Transitions);

public sealed record FlowTransition(
    string Key,
    MappingReference Mapping);

/// <summary>
/// The mapping section of a transition.
/// <see cref="Helpers"/> lists helper component keys the mapping depends on;
/// <see cref="Location"/> points at the mapping .csx;
/// <see cref="AllowedAssemblies"/> declares extra sandbox-permitted assemblies this
/// mapping's helpers/script may reference, merged on top of the global baseline.
/// </summary>
public sealed record MappingReference(
    [property: JsonPropertyName("helpers")] List<string> Helpers,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("allowedAssemblies")] List<string>? AllowedAssemblies = null);
