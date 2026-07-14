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
/// The referenced <see cref="Mapping"/> script implements <see cref="IEventMapping"/>.
/// </remarks>
public sealed class Event
{
    private Event()
    {
    }

    [JsonConstructor]
    public Event(ScriptCode mapping)
    {
        Mapping = mapping;
    }

    /// <summary>
    /// The mapping script (implements <see cref="IEventMapping"/>) that turns the raw event
    /// payload into an <c>InstanceKey</c> + <c>Body</c>.
    /// </summary>
    public ScriptCode Mapping { get; private set; } = null!;
}
