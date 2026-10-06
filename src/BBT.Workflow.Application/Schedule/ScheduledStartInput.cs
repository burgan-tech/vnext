using System.Collections.Generic;

namespace BBT.Workflow.Schedule;

/// <summary>
/// One tick of an external scheduler asking for a new instance of <see cref="Workflow"/>.
/// </summary>
/// <remarks>
/// Shaped by what a Dapr cron binding can actually send. The binding delivers an EMPTY body and
/// forwards none of the component's own metadata, so everything the runtime needs arrives either in
/// the route (domain, workflow, and the query string below) or in the two headers the binding adds
/// (<c>readtimeutc</c>, <c>timezone</c>).
/// </remarks>
public sealed class ScheduledStartInput
{
    /// <summary>Target domain, from the route.</summary>
    public required string Domain { get; init; }

    /// <summary>Target workflow key, from the route.</summary>
    public required string Workflow { get; init; }

    /// <summary>
    /// Identity of the schedule that fired, from the <c>scheduleId</c> query parameter. Keeps two
    /// components targeting the same workflow apart when their schedules coincide. Optional; without
    /// it, coincident ticks of the same workflow collapse into one instance.
    /// </summary>
    public string? ScheduleId { get; init; }

    /// <summary>
    /// Seed data for the new instance, taken from the remaining query parameters. Values are always
    /// strings — a query string carries no types — so a workflow whose start schema requires a number
    /// or an object cannot be seeded this way.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Request headers, lower-cased. Carries <c>readtimeutc</c>, which is what makes the derived
    /// instance key identical across replicas firing the same tick.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Headers { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// Whether to block until the pipeline reaches a rest point. Defaults to false: a scheduler wants
    /// a fast acknowledgement, and Dapr's cron binding discards the response regardless.
    /// </summary>
    public bool Sync { get; init; }
}
