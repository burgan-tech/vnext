using System.Text.Json;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Request body of the internal-only SubFlow transition relay endpoint.
/// <para>
/// The relay cannot reuse the public transition endpoint because it must carry
/// <see cref="ChainReserved"/>, and that endpoint copies caller headers unfiltered while
/// serializing only the data element — a claim exposed there would be forgeable by any client and
/// would defeat the Busy-as-mutex guarantee. Carrying it in this body, on an endpoint protected by
/// network isolation (same posture as the related-data endpoints), keeps it server-internal.
/// </para>
/// </summary>
public record SubflowForwardInput
{
    /// <summary>Transition data attributes forwarded from the parent request.</summary>
    public JsonElement? Attributes { get; init; }

    /// <summary>Instance key forwarded from the parent request.</summary>
    public string? Key { get; init; }

    /// <summary>Instance tags forwarded from the parent request.</summary>
    public string[]? Tags { get; init; }

    /// <summary>Instance stage forwarded from the parent request.</summary>
    public string? Stage { get; init; }

    /// <summary>Whether the forwarded transition must execute synchronously.</summary>
    public bool Sync { get; init; }

    /// <summary>
    /// True when the originating accept reserved this SubFlow chain's Busy flag down to the leaf,
    /// so the target must admit the relay as an owner re-entry instead of rejecting the pre-set
    /// Busy with a 409.
    /// </summary>
    public bool ChainReserved { get; init; }

    /// <summary>Business correlation id of the originating execution chain.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Route values forwarded from the parent request.</summary>
    public Dictionary<string, string?> RouteValues { get; init; } = new();
}
