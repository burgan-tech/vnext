namespace BBT.Workflow.Scripting.Contracts;

/// <summary>
/// Defines the contract for mapping an inbound external event (e.g. a pub/sub message or input-binding
/// payload) onto a workflow action. Domain teams author the implementation as a C# script and ship it
/// (base64 / inline / reference) in the <c>event.mapping</c> code field of a workflow or transition;
/// the runtime compiles it and runs <see cref="Handler"/> when an event is received.
/// </summary>
/// <remarks>
/// <para>
/// The raw event payload is exposed via <see cref="ScriptContext.EventPayload"/>. The handler is
/// responsible for two things:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Correlation</strong>: extract the business key that identifies the target
/// workflow instance (e.g. <c>userId</c>, <c>orderId</c>) into <see cref="EventMappingResult.InstanceKey"/>.</description></item>
/// <item><description><strong>Payload mapping</strong>: shape the data that should become the new
/// instance's initial data (for <c>action=start</c>) or the transition's input data
/// (for <c>action=transition</c>) into <see cref="EventMappingResult.Body"/>.</description></item>
/// </list>
/// <para>
/// The implementation should be deterministic and side-effect free; it only transforms the payload.
/// </para>
/// </remarks>
public interface IEventMapping
{
    /// <summary>
    /// Maps the raw event payload (available via <see cref="ScriptContext.EventPayload"/>) into a
    /// correlation key and a body.
    /// </summary>
    /// <param name="context">
    /// The script context. <see cref="ScriptContext.EventPayload"/> holds the raw inbound event;
    /// <see cref="ScriptContext.Headers"/> and <see cref="ScriptContext.Workflow"/> are also available.
    /// </param>
    /// <returns>An <see cref="EventMappingResult"/> with the resolved <c>InstanceKey</c> and mapped <c>Body</c>.</returns>
    Task<EventMappingResult> Handler(ScriptContext context);
}

/// <summary>
/// The result produced by an <see cref="IEventMapping"/>: the correlation key plus the mapped body.
/// </summary>
public sealed class EventMappingResult
{
    /// <summary>
    /// Business key used to correlate the event to a workflow instance. For <c>action=start</c> it
    /// becomes the new instance's key; for <c>action=transition</c> it is used to find the active instance.
    /// </summary>
    public string? InstanceKey { get; set; }

    /// <summary>
    /// The mapped payload. Used as the new instance's initial data (start) or the transition's input data (transition).
    /// </summary>
    public dynamic? Body { get; set; }
}
