using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Generic, extensible container for declarative directives that steer the client-side
/// workflow manager when an instance enters a state. Today it carries a single facet
/// (<see cref="LongPoll"/>); future facets (e.g. polling cadence, navigation, refresh)
/// are added as new siblings here without touching the pipeline or State-function code,
/// which read only through the <c>State</c> helper accessors.
/// </summary>
public sealed class StateInteraction
{
    private StateInteraction()
    {
    }

    [JsonConstructor]
    private StateInteraction(LongPollInteraction? longPoll)
    {
        LongPoll = longPoll;
    }

    /// <summary>
    /// Long-poll termination directive. Null when the state does not steer long polling.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("longPoll")]
    public LongPollInteraction? LongPoll { get; private set; }
}
