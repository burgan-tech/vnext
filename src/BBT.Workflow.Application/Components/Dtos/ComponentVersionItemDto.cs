namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// One published version of a component, without its body.
/// </summary>
public sealed class ComponentVersionItemDto
{
    /// <summary>The version as stored, build metadata included.</summary>
    public required string Version { get; init; }

    /// <summary>When the version was published (its data row was written).</summary>
    public required DateTime PublishedAt { get; init; }

    /// <summary>
    /// Whether this row carries the store's latest flag: the highest version written so far, and so the
    /// definition the instance API shows for the component (<c>GET {domain}/workflows/sys-flows/instances/{key}</c>).
    /// Not publish order — publishing an older build leaves the flag where it is — and not necessarily what a
    /// request resolves to: <see cref="ComponentVersionListDto.Latest"/> is.
    /// </summary>
    public required bool LatestRow { get; init; }
}
