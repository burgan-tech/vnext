namespace BBT.Workflow.Events.Hooks;

/// <summary>
/// Marker attribute indicating that an event type supports hooks.
/// Events decorated with this attribute can have hooks registered via DI.
/// </summary>
/// <remarks>
/// <para>
/// This attribute is used declaratively to indicate that an event type
/// is designed to work with the event hook system. When registering hooks
/// via <c>services.AddEventHook&lt;TEvent, THook&gt;()</c>, the system will
/// validate that the event type has this attribute.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EventHookAttribute(
    EventHookMode mode = EventHookMode.HandledOrFallback) : Attribute
{
    /// <summary>
    /// Gets the hook execution mode for the decorated event.
    /// </summary>
    public EventHookMode Mode { get; } = mode;
}
