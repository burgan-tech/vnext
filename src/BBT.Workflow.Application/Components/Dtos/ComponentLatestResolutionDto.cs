namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// The version a request with no version (or <c>latest</c>) resolves to, decided by the same rules as
/// version resolution (<c>DefaultVersionResolver</c>).
/// </summary>
public sealed class ComponentLatestResolutionDto
{
    /// <summary>The resolved version; <c>null</c> unless <see cref="Outcome"/> is <c>resolved</c>.</summary>
    public string? Version { get; init; }

    /// <summary><c>resolved</c>, <c>notFound</c> or <c>pointerInvalid</c> (the pointer matches no published version).</summary>
    public required string Outcome { get; init; }

    /// <summary><c>automatic</c> (no pointer: the highest published version) or <c>default</c> (the pointer decided).</summary>
    public required string Source { get; init; }
}
