using System.Text.Json.Serialization;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Definitions.Events;

/// <summary>
/// Declares how an inbound external event is mapped before it acts on a workflow.
/// </summary>
/// <remarks>
/// Defined in two places, both optional and independent:
/// <list type="bullet">
/// <item><description>At <strong>workflow</strong> level (<c>attributes.event</c>) — used by <c>action=start</c>
/// to create a new instance from an event.</description></item>
/// <item><description>At <strong>transition</strong> level (<c>transition.event</c>) — used by
/// <c>action=transition</c> to advance an existing instance. Its presence is what makes a transition
/// reachable by an event; it does not remove the transition's existing (non-event) trigger.</description></item>
/// </list>
/// The event is mapped either by the <see cref="Mapping"/> script (implements <see cref="IEventMapping"/>) or
/// by the declarative <see cref="Input"/> block. Publish rejects an event declaring both; a stored definition
/// holding both runs the script.
/// </remarks>
public sealed class Event
{
    private Event()
    {
    }

    /// <summary>Creates an event mapped by a script, a declarative block, or (stored only) both.</summary>
    [JsonConstructor]
    public Event(ScriptCode? mapping, MappingBlock? input = null)
    {
        Mapping = mapping;
        Input = input;
    }

    /// <summary>
    /// The mapping script (implements <see cref="IEventMapping"/>) that turns the raw event
    /// payload into an <c>InstanceKey</c> + <c>Body</c>. <c>null</c> when the event uses <see cref="Input"/>.
    /// </summary>
    public ScriptCode? Mapping { get; private set; }

    /// <summary>
    /// Optional declarative block that shapes the target document <c>{ key, attributes }</c> from the event
    /// payload without a script. Mutually exclusive with <see cref="Mapping"/>.
    /// </summary>
    [JsonPropertyName("input")]
    public MappingBlock? Input { get; private set; }
}
