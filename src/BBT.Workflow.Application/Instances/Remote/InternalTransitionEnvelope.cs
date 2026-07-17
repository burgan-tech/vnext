using System.Text.Json.Serialization;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.Instances.Remote;

/// <summary>
/// Typed body used only for internal remote transitions that carry terminal-cascade context.
/// Unmarked public transition requests continue to use their existing raw/standard payload shape.
/// </summary>
public sealed class InternalTransitionEnvelope
{
    public const string HeaderName = "X-Vnext-Internal-Transition-Envelope";
    public const string HeaderValue = "1";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TransitionDataInput? Data { get; init; }

    public required TerminationContext Termination { get; init; }
}
