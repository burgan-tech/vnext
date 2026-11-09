using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Scripting;

/// <summary>
/// One candidate transition an agent may choose (issue #1105).
/// </summary>
/// <param name="Key">Transition key.</param>
/// <param name="When">When the transition applies, as the model reads it.</param>
/// <param name="WhenSource">Where <paramref name="When"/> came from: <c>hintsMapping</c>, <c>hint</c>, <c>description</c>, <c>label</c> or <c>key</c>.</param>
public sealed record AgentCandidate(string Key, string When, string WhenSource);

/// <summary>
/// What an agent run produced, as decision scripts see it.
/// </summary>
/// <param name="Completed">Whether the model returned a final answer.</param>
/// <param name="Output">The structured output.</param>
/// <param name="ModelDecision">The <c>decision</c> member of the output, when present.</param>
/// <param name="Reason">The <c>reason</c> member of the output, when present.</param>
/// <param name="FailureReason">Why the run did not complete.</param>
public sealed record AgentRunOutcome(
    bool Completed,
    JsonElement? Output,
    string? ModelDecision,
    string? Reason,
    string? FailureReason);

/// <summary>
/// Builds the user prompt of an <see cref="StateType.AiAgent"/> state from the script context
/// (the C# alternative of <c>agent.prompt.template</c>).
/// </summary>
public interface IAgentPromptMapping
{
    /// <summary>Returns the user prompt.</summary>
    Task<string> Handler(ScriptContext context);
}

/// <summary>
/// Chooses the next transition of an agent state in <c>decision.mode: script</c>.
/// Return one of the candidate keys; any other value (or null) takes the fallback.
/// </summary>
public interface IAgentDecisionMapping
{
    /// <summary>Returns the chosen transition key.</summary>
    Task<string?> Handler(AgentRunOutcome outcome, IReadOnlyList<AgentCandidate> candidates, ScriptContext context);
}

/// <summary>
/// Produces the "when" texts of an agent state's candidates (<c>decision.hintsMapping</c>).
/// Keys that are not candidates are ignored.
/// </summary>
public interface IAgentDecisionHintMapping
{
    /// <summary>Returns hints keyed by transition key.</summary>
    Task<IDictionary<string, string>> Handler(IReadOnlyList<AgentCandidate> candidates, ScriptContext context);
}

/// <summary>
/// A C# agent tool (<see cref="AgentToolKind.Script"/> with a C# location). Receives the model's
/// arguments and returns the tool result, serialized to JSON for the model. Must not write instance data.
/// </summary>
public interface IToolHandler
{
    /// <summary>Runs the tool.</summary>
    Task<object?> Handler(JsonElement args, ScriptContext context);
}
