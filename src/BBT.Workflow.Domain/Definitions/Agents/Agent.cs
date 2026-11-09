using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// An agent component (<c>sys-agents</c>, issue #1105): a reusable, versioned AI agent definition
/// distributed with the domain like flows, tasks and mappings. A workflow state of type
/// <see cref="StateType.AiAgent"/> references it through <see cref="AgentStateBinding.Agent"/>.
/// </summary>
/// <remarks>
/// The component never carries an endpoint, a model name or a secret: <see cref="Provider"/> names a
/// provider profile configured per environment (<c>AiAgent:Providers:{name}</c>).
/// </remarks>
public sealed class Agent : IDomainEntity, IReferenceSetter
{
    [JsonConstructor]
    private Agent()
    {
        Flow = RuntimeSysSchemaInfo.Agents;
    }

    /// <summary>Readable key of the record.</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>The component flow, always <c>sys-agents</c>.</summary>
    public string Flow { get; init; }

    /// <summary>The domain the component belongs to.</summary>
    public string Domain { get; private set; } = string.Empty;

    /// <summary>The version the record was published with.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Provider profile name (<c>AiAgent:Providers:{name}</c>). Required.</summary>
    [JsonInclude] [JsonPropertyName("provider")]
    public string Provider { get; private set; } = string.Empty;

    /// <summary>Agent runtime key; <c>maf</c> by default.</summary>
    [JsonInclude] [JsonPropertyName("runtime")]
    public string Runtime { get; private set; } = AgentRuntimeKeys.Maf;

    /// <summary>Sampling settings sent to the provider.</summary>
    [JsonInclude] [JsonPropertyName("model")]
    public AgentModelSettings? Model { get; private set; }

    /// <summary>The system prompt. Never rendered from instance data.</summary>
    [JsonInclude] [JsonPropertyName("instructions")]
    public AgentInstructions? Instructions { get; private set; }

    [JsonInclude] [JsonPropertyName("tools")]
    private List<AgentTool> tools = [];

    /// <summary>Inline tool definitions.</summary>
    [JsonIgnore]
    public IReadOnlyList<AgentTool> Tools => tools;

    /// <summary>Default memory settings; a state may override them.</summary>
    [JsonInclude] [JsonPropertyName("memory")]
    public AgentMemory? Memory { get; private set; }

    /// <summary>The structured output contract. Required.</summary>
    [JsonInclude] [JsonPropertyName("output")]
    public AgentOutput? Output { get; private set; }

    /// <summary>Budgets and retry; a state may only narrow them.</summary>
    [JsonInclude] [JsonPropertyName("policy")]
    public AgentPolicy? Policy { get; private set; }

    /// <summary>What the run journal keeps.</summary>
    [JsonInclude] [JsonPropertyName("audit")]
    public AgentAudit? Audit { get; private set; }

    /// <summary>Semantic version without build metadata.</summary>
    [JsonIgnore]
    public string SemanticVersion => Regex.Match(Version, @"^([^+]+)").Groups[1].Value;

    /// <summary>The component type key, <c>sys-agents</c>.</summary>
    public static string ComponentTypeKey => RuntimeSysSchemaInfo.Agents;

    /// <inheritdoc />
    public string ComponentKey => ComponentTypeKey;

    /// <summary>Builds the cache key of an agent component.</summary>
    public static string GenerateCacheKey(string domain, string flow, string key, string version)
        => $"{nameof(Agent)}:{domain}:{flow}:{key}:{version}";

    /// <inheritdoc />
    public void SetReference(IReference reference)
    {
        Key = Check.NotNullOrWhiteSpace(reference.Key, nameof(Key), WorkflowConstants.MaxKeyLength);
        Domain = Check.NotNullOrWhiteSpace(reference.Domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
        Version = Check.NotNullOrWhiteSpace(reference.Version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    /// <summary>Creates an agent in code (tests, dry-run fixtures).</summary>
    public static Agent Create(
        string provider,
        AgentInstructions instructions,
        AgentOutput output,
        AgentPolicy policy,
        IEnumerable<AgentTool>? tools = null,
        AgentMemory? memory = null,
        AgentModelSettings? model = null,
        AgentAudit? audit = null,
        string? runtime = null) => new()
    {
        Provider = provider,
        Instructions = instructions,
        Output = output,
        Policy = policy,
        tools = tools?.ToList() ?? [],
        Memory = memory,
        Model = model,
        Audit = audit,
        Runtime = runtime ?? AgentRuntimeKeys.Maf
    };
}

/// <summary>Well-known agent runtime keys.</summary>
public static class AgentRuntimeKeys
{
    /// <summary>Microsoft Agent Framework runtime.</summary>
    public const string Maf = "maf";
}
