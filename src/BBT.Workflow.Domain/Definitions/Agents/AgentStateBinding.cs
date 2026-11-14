using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// The <c>agent</c> block of an <see cref="StateType.AiAgent"/> state (issue #1105).
/// </summary>
public sealed record AgentStateBinding
{
    /// <summary>The <c>sys-agents</c> component reference.</summary>
    public Reference? Agent { get; init; }

    /// <summary>The user prompt.</summary>
    public AgentPrompt? Prompt { get; init; }

    /// <summary>Where the structured output lands in instance data.</summary>
    public AgentStateOutput? Output { get; init; }

    /// <summary>Overrides the component's memory for this state.</summary>
    public AgentMemory? Memory { get; init; }

    /// <summary>Tool selection for this state.</summary>
    public AgentToolSelection? Tools { get; init; }

    /// <summary>How the next transition is chosen. Required.</summary>
    public AgentDecision? Decision { get; init; }

    /// <summary>Narrows the component's policy.</summary>
    public AgentPolicy? Policy { get; init; }

    /// <summary>
    /// How several agents work in this state. <c>single</c> (default): only <see cref="Agent"/> runs.
    /// <c>sequential</c> / <c>concurrent</c>: the <see cref="Agents"/> run first (in order, each seeing the
    /// outputs before it / in parallel on the same prompt), then <see cref="Agent"/> — the lead — runs with
    /// their outputs and produces the state's output and decision. <c>handoff</c>: the lead runs as a router
    /// and names one participant, which then produces the state's output and decision.
    /// </summary>
    public AgentTeamMode? Mode { get; init; }

    /// <summary>Participants of a multi-agent state (<see cref="Mode"/> other than <c>single</c>).</summary>
    public List<AgentParticipant>? Agents { get; init; }

    /// <summary>The effective team mode.</summary>
    [JsonIgnore]
    public AgentTeamMode EffectiveMode => Mode ?? AgentTeamMode.Single;
}

/// <summary>How the agents of a state work together.</summary>
public enum AgentTeamMode
{
    /// <summary>One agent.</summary>
    Single = 0,

    /// <summary>Participants in order, then the lead.</summary>
    Sequential = 1,

    /// <summary>Participants in parallel, then the lead.</summary>
    Concurrent = 2,

    /// <summary>The lead routes to one participant, which answers.</summary>
    Handoff = 3
}

/// <summary>A participant agent of a multi-agent state.</summary>
public sealed record AgentParticipant
{
    /// <summary>Name, unique in the state (<c>^[a-zA-Z0-9_-]{1,64}$</c>); the lead sees outputs and routes by it.</summary>
    public string? Name { get; init; }

    /// <summary>The <c>sys-agents</c> component.</summary>
    public Reference? Agent { get; init; }

    /// <summary>What the lead is told this participant is for (handoff).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// The user prompt of an agent state: a <see cref="Template"/> with <c>{{ path }}</c> placeholders over
/// the instance data, or a C# mapping (<see cref="Location"/> + <see cref="Code"/>) implementing
/// <c>IAgentPromptMapping</c>.
/// </summary>
public sealed record AgentPrompt
{
    /// <summary>Inline template.</summary>
    public string? Template { get; init; }

    /// <summary>C# prompt mapping, as a script.</summary>
    public string? Location { get; init; }

    /// <summary>Packaged C# code.</summary>
    public string? Code { get; init; }

    /// <summary>Encoding of <see cref="Code"/>.</summary>
    public string? Encoding { get; init; }

    /// <summary>
    /// Seed messages sent before the user prompt on every run (few-shot examples, extra instructions).
    /// Each <c>content</c> is a template with the same placeholders as <see cref="Template"/>.
    /// </summary>
    public List<AgentPromptMessage>? Messages { get; init; }

    /// <summary>
    /// Named data blocks appended to the user prompt, each rendered as JSON inside
    /// <c>&lt;data name="…"&gt;</c>, so the model reads them as data and not as instructions. The value is a
    /// path: <c>data.customer</c>, <c>body.amount</c>, <c>instance.key</c>.
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }

    /// <summary>True when the prompt is a C# mapping.</summary>
    [JsonIgnore]
    public bool IsScript => string.IsNullOrEmpty(Template) && !string.IsNullOrEmpty(Code);

    /// <summary>The prompt mapping as a <see cref="ScriptCode"/>.</summary>
    public ScriptCode ToScriptCode() =>
        string.Equals(Encoding, "NAT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Encoding, "Native", StringComparison.OrdinalIgnoreCase)
            ? ScriptCode.FromNative(Code ?? string.Empty, Location)
            : ScriptCode.FromBase64(Code ?? string.Empty, Location);
}

/// <summary>A seed message of an agent prompt.</summary>
public sealed record AgentPromptMessage
{
    /// <summary><c>system</c>, <c>user</c> or <c>assistant</c>.</summary>
    public string? Role { get; init; }

    /// <summary>Template content.</summary>
    public string? Content { get; init; }
}

/// <summary>Output placement of an agent state.</summary>
public sealed record AgentStateOutput
{
    /// <summary>JSON path (<c>$.triage</c>) the structured output is merged under.</summary>
    public string? Path { get; init; }
}

/// <summary>Per-state tool selection.</summary>
public sealed record AgentToolSelection
{
    /// <summary>Tool names hidden from the model in this state.</summary>
    public List<string>? Exclude { get; init; }
}

/// <summary>Who chooses the next transition.</summary>
public enum AgentDecisionMode
{
    /// <summary>The model chooses among the candidates.</summary>
    Model = 0,

    /// <summary>The transitions' own rules choose over the agent's output.</summary>
    Rules = 1,

    /// <summary>A C# <c>IAgentDecisionMapping</c> chooses.</summary>
    Script = 2
}

/// <summary>Decision settings of an agent state.</summary>
public sealed record AgentDecision
{
    /// <summary>Decision mode; <see cref="AgentDecisionMode.Model"/> by default.</summary>
    public AgentDecisionMode? Mode { get; init; }

    /// <summary>Automatic transitions left out of the candidate set.</summary>
    public List<string>? Exclude { get; init; }

    /// <summary>"When" texts per candidate key; win over the transition description.</summary>
    public Dictionary<string, string>? Hints { get; init; }

    /// <summary>C# <c>IAgentDecisionHintMapping</c> producing hints.</summary>
    public ScriptCode? HintsMapping { get; init; }

    /// <summary>C# <c>IAgentDecisionMapping</c> for <see cref="AgentDecisionMode.Script"/>.</summary>
    public ScriptCode? Mapping { get; init; }

    /// <summary>Automatic transition taken when the run cannot decide. Required.</summary>
    public string? Fallback { get; init; }

    /// <summary>Automatic transition taken when a tool asks for approval (Faz 2).</summary>
    public string? OnApprovalRequest { get; init; }

    /// <summary>Automatic transition taken when <c>policy.maxEntries</c> is reached.</summary>
    public string? OnExhausted { get; init; }

    /// <summary>The effective mode.</summary>
    [JsonIgnore]
    public AgentDecisionMode EffectiveMode => Mode ?? AgentDecisionMode.Model;

    /// <summary>The keys reserved for the decision's own exits.</summary>
    [JsonIgnore]
    public IEnumerable<string> ReservedKeys
    {
        get
        {
            if (!string.IsNullOrEmpty(Fallback)) yield return Fallback!;
            if (!string.IsNullOrEmpty(OnApprovalRequest)) yield return OnApprovalRequest!;
            if (!string.IsNullOrEmpty(OnExhausted)) yield return OnExhausted!;
        }
    }
}
