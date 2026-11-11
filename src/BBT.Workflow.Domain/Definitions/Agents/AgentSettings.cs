using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>Sampling settings an agent sends to its provider.</summary>
public sealed record AgentModelSettings
{
    /// <summary>Sampling temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Output token ceiling per model call.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Nucleus sampling.</summary>
    public double? TopP { get; init; }
}

/// <summary>
/// The agent's system prompt: inline <see cref="Text"/>, or a file the CLI inlines at package time
/// (<see cref="Location"/> with its content in <see cref="Code"/>). Never a template over instance data.
/// </summary>
public sealed record AgentInstructions
{
    /// <summary>Inline instructions.</summary>
    public string? Text { get; init; }

    /// <summary>Source file the instructions came from (informational once packaged).</summary>
    public string? Location { get; init; }

    /// <summary>Packaged file content (Base64 or plain, see <see cref="Encoding"/>).</summary>
    public string? Code { get; init; }

    /// <summary>Encoding of <see cref="Code"/>: <c>B64</c> (default) or <c>NAT</c>.</summary>
    public string? Encoding { get; init; }

    /// <summary>The resolved system prompt.</summary>
    [JsonIgnore]
    public string Resolved
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Text))
                return Text!;
            if (string.IsNullOrEmpty(Code))
                return string.Empty;
            if (string.Equals(Encoding, "NAT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Encoding, "Native", StringComparison.OrdinalIgnoreCase))
                return Code!;
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Code!));
            }
            catch (FormatException)
            {
                return Code!;
            }
        }
    }
}

/// <summary>Kind of an inline agent tool.</summary>
public enum AgentToolKind
{
    /// <summary>An existing <c>sys-tasks</c> component runs as the tool.</summary>
    Task = 1,

    /// <summary>A <see cref="ScriptCode"/> (dynamicExpresso or C#) computes the tool result.</summary>
    Script = 2,

    /// <summary>An MCP task (type 24) exposes the server's tools.</summary>
    Mcp = 3
}

/// <summary>An inline tool definition of an agent.</summary>
public sealed record AgentTool
{
    /// <summary>Tool kind.</summary>
    public AgentToolKind Kind { get; init; }

    /// <summary>Name the model sees (<c>^[a-zA-Z0-9_-]{1,64}$</c>); not used by kind 3.</summary>
    public string? Name { get; init; }

    /// <summary>What the tool does, for the model; not used by kind 3.</summary>
    public string? Description { get; init; }

    /// <summary>JSON schema of the arguments (<c>type: object</c>); not used by kind 3.</summary>
    public JsonElement? Parameters { get; init; }

    /// <summary>Human approval before the call (Faz 2). A tool that requires it fails closed in Faz 1.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Caller roles that may see this tool; empty means every caller.</summary>
    public List<string>? Roles { get; init; }

    /// <summary>Per-tool timeout.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Tool result characters the model receives.</summary>
    public int? ResultMaxChars { get; init; }

    /// <summary>What runs when the model calls the tool.</summary>
    public AgentToolBinding Binding { get; init; } = new();
}

/// <summary>The binding of an inline tool; which members apply depends on <see cref="AgentTool.Kind"/>.</summary>
public sealed record AgentToolBinding
{
    /// <summary>Kind 1 and 3: the task reference.</summary>
    public Reference? Task { get; init; }

    /// <summary>Kind 1: optional input/output mapping (<c>IMapping</c>); without it the arguments become the body.</summary>
    public ScriptCode? Mapping { get; init; }

    /// <summary>Kind 2: the script.</summary>
    public ScriptCode? Code { get; init; }

    /// <summary>Kind 3: server tool names exposed to the model; empty exposes none.</summary>
    public List<string>? Allow { get; init; }

    /// <summary>Kind 3: prefix added to every server tool name.</summary>
    public string? Prefix { get; init; }

    /// <summary>Kind 3: how long a server's tool list is reused.</summary>
    public int? DiscoveryCacheMinutes { get; init; }
}

/// <summary>Conversation memory scope.</summary>
public enum AgentMemoryMode
{
    /// <summary>Every entry starts clean.</summary>
    None = 0,

    /// <summary>Per instance and state.</summary>
    State = 1,

    /// <summary>Per instance, shared by the flow's agent states.</summary>
    Flow = 2,

    /// <summary>Across instances, keyed by a rendered key.</summary>
    Key = 3
}

/// <summary>Where the conversation lives.</summary>
public enum AgentMemoryStorage
{
    /// <summary>The runtime stores the messages.</summary>
    Local = 0,

    /// <summary>The provider stores the conversation; only its id is kept.</summary>
    Provider = 1
}

/// <summary>How a local session is kept within <see cref="AgentMemory.MaxMessages"/>.</summary>
public enum AgentMemoryCompaction
{
    /// <summary>Drop the oldest messages (never starting the kept window on a tool exchange).</summary>
    Trim = 0,

    /// <summary>Replace the oldest messages with one model-written summary (one extra provider call when it triggers).</summary>
    Summarize = 1
}

/// <summary>Conversation memory settings.</summary>
public sealed record AgentMemory
{
    /// <summary>Memory scope.</summary>
    public AgentMemoryMode? Mode { get; init; }

    /// <summary>Key template for <see cref="AgentMemoryMode.Key"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Message store.</summary>
    public AgentMemoryStorage? Storage { get; init; }

    /// <summary>Messages kept (oldest trimmed or summarized first).</summary>
    public int? MaxMessages { get; init; }

    /// <summary>How the session is kept within <see cref="MaxMessages"/> (local storage only).</summary>
    public AgentMemoryCompaction? Compaction { get; init; }

    /// <summary>Days a session is kept after its last write; an instance session is deleted earlier, when its instance completes or is cancelled.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>Reset triggers: <c>agentVersionChange</c>, <c>instructionsChange</c>.</summary>
    public List<string>? ResetOn { get; init; }

    /// <summary>Overlays <paramref name="over"/> on this instance.</summary>
    public AgentMemory Overlay(AgentMemory? over) => over is null ? this : new AgentMemory
    {
        Mode = over.Mode ?? Mode,
        Key = over.Key ?? Key,
        Storage = over.Storage ?? Storage,
        MaxMessages = over.MaxMessages ?? MaxMessages,
        Compaction = over.Compaction ?? Compaction,
        RetentionDays = over.RetentionDays ?? RetentionDays,
        ResetOn = over.ResetOn ?? ResetOn
    };

    /// <summary>Default memory: per state, local, 40 messages, 30 days.</summary>
    public static readonly AgentMemory Default = new()
    {
        Mode = AgentMemoryMode.State,
        Storage = AgentMemoryStorage.Local,
        MaxMessages = 40,
        Compaction = AgentMemoryCompaction.Trim,
        RetentionDays = 30,
        ResetOn = ["agentVersionChange"]
    };
}

/// <summary>Structured output contract of an agent.</summary>
public sealed record AgentOutput
{
    /// <summary>JSON schema of the produced object (<c>type: object</c>).</summary>
    public JsonElement? Schema { get; init; }
}

/// <summary>What happens after provider retries are exhausted.</summary>
public enum AgentProviderErrorAction
{
    /// <summary>Take the decision fallback.</summary>
    Fallback = 0,

    /// <summary>Fail the transition so the error boundary decides.</summary>
    Fault = 1
}

/// <summary>Provider-call retry inside one run.</summary>
public sealed record AgentRetryPolicy
{
    /// <summary>Attempts including the first.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>Base backoff.</summary>
    public int? BackoffMs { get; init; }

    /// <summary>Retried failures: <c>429</c>, <c>5xx</c>, <c>timeout</c>.</summary>
    public List<string>? On { get; init; }
}

/// <summary>Budgets of an agent run. A state's policy may only narrow the component's.</summary>
public sealed record AgentPolicy
{
    /// <summary>Model round trips.</summary>
    public int? MaxIterations { get; init; }

    /// <summary>Tool calls.</summary>
    public int? MaxToolCalls { get; init; }

    /// <summary>Total tokens.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Whole-run timeout.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Per tool timeout.</summary>
    public int? ToolTimeoutSeconds { get; init; }

    /// <summary>Tool result characters the model receives.</summary>
    public int? ResultMaxChars { get; init; }

    /// <summary>Re-entry count before <c>onExhausted</c> (state memory).</summary>
    public int? MaxEntries { get; init; }

    /// <summary>Provider retry.</summary>
    public AgentRetryPolicy? Retry { get; init; }

    /// <summary>After retries are exhausted.</summary>
    public AgentProviderErrorAction? OnProviderError { get; init; }
}

/// <summary>Transcript retention.</summary>
public enum AgentTranscriptMode
{
    /// <summary>No transcript.</summary>
    None = 0,

    /// <summary>Final answer and tool call summaries.</summary>
    Summary = 1,

    /// <summary>Every message.</summary>
    Full = 2
}

/// <summary>What the run journal keeps.</summary>
public sealed record AgentAudit
{
    /// <summary>Transcript retention.</summary>
    public AgentTranscriptMode? Transcript { get; init; }

    /// <summary>Whether tool results are kept in the tool-call rows.</summary>
    public bool? PersistToolResults { get; init; }

    /// <summary>JSON paths masked before anything is kept.</summary>
    public List<string>? Redact { get; init; }
}
