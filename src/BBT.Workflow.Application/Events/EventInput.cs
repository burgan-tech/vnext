using System.Text.Json;
using BBT.Workflow.Definitions.Events;

namespace BBT.Workflow.Events;


/// <summary>
/// Normalized inbound event request, built by the EventController from the route + query + body.
/// </summary>
public sealed class EventInput
{
    /// <summary>Target domain (route).</summary>
    public required string Domain { get; init; }

    /// <summary>Target workflow key (route).</summary>
    public required string Workflow { get; init; }

    /// <summary>Whether the event starts a new instance or advances an existing one.</summary>
    public required EventAction Action { get; init; }

    /// <summary>Transition to execute. Required when <see cref="Action"/> is <see cref="EventAction.Transition"/>.</summary>
    public string? TransitionKey { get; init; }

    /// <summary>Raw event payload (pub/sub message / input-binding body).</summary>
    public JsonElement Payload { get; init; }

    /// <summary>Request headers, forwarded into the script context and downstream calls.</summary>
    public Dictionary<string, string?> Headers { get; init; } = new();

    /// <summary>When true, block until the pipeline completes; otherwise accept and run asynchronously.</summary>
    public bool Sync { get; init; }
}