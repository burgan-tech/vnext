using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Monitor.Instances.DTOs;

/// <summary>
/// Input for the domain-wide faulted-instances query. The time window and any business
/// filters are carried inside <see cref="Filter"/>; a bounded createdAt range is mandatory.
/// </summary>
public sealed class MonitorGetDomainFaultedInput : IHasDomain
{
    /// <summary>The tenant/domain key whose workflow schemas are scanned.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// GraphQL-style filter JSON. Must contain a bounded createdAt range (lower and upper bound)
    /// and must not contain a status condition (status is fixed to Faulted by this endpoint).
    /// </summary>
    public string? Filter { get; set; }
}
