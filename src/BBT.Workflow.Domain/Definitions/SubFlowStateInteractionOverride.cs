using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Parent-supplied override of a child state's <c>interaction</c> container. Mirrors
/// <see cref="StateInteraction"/>: one facet today (<see cref="LongPoll"/>).
/// </summary>
public sealed class SubFlowStateInteractionOverride
{
    private SubFlowStateInteractionOverride()
    {
    }

    [JsonConstructor]
    private SubFlowStateInteractionOverride(SubFlowLongPollOverride? longPoll)
    {
        LongPoll = longPoll;
    }

    /// <summary>
    /// Long-poll override. Null when the parent does not tune the child's long-poll.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("longPoll")]
    public SubFlowLongPollOverride? LongPoll { get; private set; }

    public static SubFlowStateInteractionOverride Create(SubFlowLongPollOverride? longPoll = null) => new(longPoll);
}
