namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// Full detail of a single runtime component. <see cref="Definition"/> carries the
/// component object as stored in the runtime (serialized as JSON by the API layer).
/// </summary>
public sealed class ComponentDetailDto
{
    /// <summary>The component type URL token (e.g. <c>workflows</c>).</summary>
    public required string Type { get; init; }

    /// <summary>The unique component key within the domain.</summary>
    public required string Key { get; init; }

    /// <summary>The domain the component belongs to.</summary>
    public required string Domain { get; init; }

    /// <summary>The component version (SemVer).</summary>
    public required string Version { get; init; }

    /// <summary>The full component definition object.</summary>
    public required object Definition { get; init; }
}
