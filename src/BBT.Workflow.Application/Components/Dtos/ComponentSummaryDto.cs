namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// Lightweight projection of a runtime component for list responses. Carries only
/// identification metadata — never the component body or <c>.csx</c> code.
/// </summary>
public sealed class ComponentSummaryDto
{
    /// <summary>The component type URL token (e.g. <c>workflows</c>).</summary>
    public required string Type { get; init; }

    /// <summary>The runtime component-type key (e.g. <c>sys-flows</c>).</summary>
    public required string ComponentTypeKey { get; init; }

    /// <summary>The unique component key within the domain.</summary>
    public required string Key { get; init; }

    /// <summary>The domain the component belongs to.</summary>
    public required string Domain { get; init; }

    /// <summary>The component version (SemVer).</summary>
    public required string Version { get; init; }

    /// <summary>Creation timestamp when the component exposes one; otherwise <c>null</c>.</summary>
    public DateTime? CreatedAt { get; init; }
}
