using Microsoft.Extensions.Options;

namespace BBT.Workflow.Agents;

/// <summary>
/// Environment configuration of AI agent states (section <c>AiAgent</c>, issue #1105). Provider
/// endpoints, models and secrets live here, never in a component; the global ceilings bound every
/// agent component's <c>policy</c>.
/// </summary>
public sealed class AiAgentOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "AiAgent";

    /// <summary>
    /// Kill switch. When false, an AiAgent state never calls a provider: it records a disabled run and
    /// takes its <c>decision.fallback</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Provider profiles by name (the component's <c>provider</c>).</summary>
    public Dictionary<string, AgentProviderProfile> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whole-run ceiling.</summary>
    public int MaxRunSeconds { get; set; } = 120;

    /// <summary>Model round-trip ceiling.</summary>
    public int MaxIterations { get; set; } = 12;

    /// <summary>Tool-call ceiling.</summary>
    public int MaxToolCalls { get; set; } = 20;

    /// <summary>Token ceiling.</summary>
    public int MaxTokens { get; set; } = 100_000;

    /// <summary>Stored session ceiling; older turns are trimmed beyond it.</summary>
    public int MaxSessionBytes { get; set; } = 256 * 1024;

    /// <summary>Stored transcript ceiling; a larger transcript falls back to a summary.</summary>
    public int MaxTranscriptBytes { get; set; } = 1024 * 1024;

    /// <summary>Concurrent runs per process; a run that finds no slot takes the fallback.</summary>
    public int MaxConcurrentRuns { get; set; } = 32;

    /// <summary>How often expired sessions are swept; 0 disables the sweeper.</summary>
    public int SessionSweepIntervalMinutes { get; set; } = 60;

    /// <summary>Dry-run endpoint settings.</summary>
    public AgentDryRunOptions DryRun { get; set; } = new();
}

/// <summary>A provider profile.</summary>
public sealed class AgentProviderProfile
{
    /// <summary><c>openai-compatible</c> (OpenAI, Azure OpenAI v1, vLLM, Ollama) or <c>local</c> (deterministic test provider).</summary>
    public string Kind { get; set; } = AgentProviderKinds.OpenAiCompatible;

    /// <summary>Base URL of an OpenAI-compatible endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name.</summary>
    public string? Model { get; set; }

    /// <summary>Configuration key (or Vault-loaded secret name) holding the API key.</summary>
    public string? ApiKeySecretRef { get; set; }

    /// <summary><c>jsonSchema</c> (native structured output) or <c>json</c> (JSON mode + schema in the instructions).</summary>
    public string StructuredOutput { get; set; } = "jsonSchema";

    /// <summary>Whether the provider can hold conversations (<c>memory.storage: provider</c>).</summary>
    public bool SupportsServiceSessions { get; set; }
}

/// <summary>Provider kinds.</summary>
public static class AgentProviderKinds
{
    /// <summary>OpenAI chat-completions compatible endpoint.</summary>
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>Deterministic in-process provider for tests and local runs.</summary>
    public const string Local = "local";
}

/// <summary>Dry-run endpoint settings.</summary>
public sealed class AgentDryRunOptions
{
    /// <summary>Caller roles allowed to dry-run an agent; empty disables the endpoint.</summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>Validates <see cref="AiAgentOptions"/> at startup.</summary>
public sealed class AiAgentOptionsValidator : IValidateOptions<AiAgentOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AiAgentOptions options)
    {
        var failures = new List<string>();
        if (options.MaxRunSeconds is < 1 or > 900) failures.Add("AiAgent:MaxRunSeconds must be 1..900.");
        if (options.MaxIterations < 1) failures.Add("AiAgent:MaxIterations must be positive.");
        if (options.MaxToolCalls < 0) failures.Add("AiAgent:MaxToolCalls cannot be negative.");
        if (options.MaxConcurrentRuns < 1) failures.Add("AiAgent:MaxConcurrentRuns must be positive.");
        foreach (var (key, profile) in options.Providers)
        {
            if (profile.Kind is not (AgentProviderKinds.OpenAiCompatible or AgentProviderKinds.Local))
                failures.Add($"AiAgent:Providers:{key}:Kind must be '{AgentProviderKinds.OpenAiCompatible}' or '{AgentProviderKinds.Local}'.");
            if (profile.Kind == AgentProviderKinds.OpenAiCompatible && string.IsNullOrWhiteSpace(profile.Model))
                failures.Add($"AiAgent:Providers:{key}:Model is required.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
