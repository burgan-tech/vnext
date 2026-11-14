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
