using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// These are function definitions that will be distributed with the flow.
/// In general, BFF and calculation methods are defined as functions.
/// </summary>
public sealed class Function : IDomainEntity, IFunctionReference, IReferenceSetter
{
    private Function()
    {
        Flow = RuntimeSysSchemaInfo.Functions;
    }

    [JsonConstructor]
    public Function(
        TaskScope scope,
        OnExecuteTask? task,
        List<OnExecuteTask>? onExecutionTasks = null,
        ScriptCode? output = null,
        List<RoleGrant>? roles = null,
        bool rawResponse = false,
        FunctionCache? cache = null,
        List<string>? verbs = null
    ) : this()
    {
        Scope = scope;
        Task = task;
        this.onExecutionTasks = onExecutionTasks ?? [];
        Output = output;
        this.roles = roles ?? [];
        RawResponse = rawResponse;
        Cache = cache;
        this.verbs = NormalizeVerbs(verbs);
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// This is information about the domain on which the stream where the record is located.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; init; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    public TaskScope Scope { get; private set; }
    [JsonInclude] public OnExecuteTask? Task { get; private set; }

    [JsonInclude] [JsonPropertyName("onExecutionTasks")]
    private List<OnExecuteTask> onExecutionTasks = [];

    /// <summary>
    /// Optional list of tasks to execute sequentially. When populated, takes precedence over <see cref="Task"/>.
    /// Each task's output is available in <c>ScriptContext</c> for subsequent tasks.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<OnExecuteTask> OnExecutionTasks => onExecutionTasks.AsReadOnly();

    /// <summary>
    /// Optional output mapping script (implements <c>IOutputHandler</c>).
    /// When present, the script is compiled at runtime and its <c>OutputHandler</c> result
    /// is used as the function response. When absent, legacy single-task extraction is used.
    /// </summary>
    [JsonInclude] [JsonPropertyName("output")]
    public ScriptCode? Output { get; private set; }

    /// <summary>
    /// When <c>true</c>, the response data is returned as-is without wrapping it in
    /// <c>{ "functionKey": data }</c>. Use this for BFF functions that already return
    /// the desired response shape. Defaults to <c>false</c> (current behaviour preserved).
    /// </summary>
    [JsonPropertyName("rawResponse")]
    public bool RawResponse { get; private set; }

    /// <summary>
    /// Optional read-through cache configuration. When set, the function's response is served from the
    /// cache on a hit (tasks skipped) and written to the cache on a miss.
    /// </summary>
    [JsonInclude] [JsonPropertyName("cache")]
    public FunctionCache? Cache { get; private set; }

    [JsonInclude] [JsonPropertyName("verbs")]
    private List<string> verbs = [];

    /// <summary>
    /// HTTP verbs this function supports, normalized to upper case.
    /// Empty means no verb restriction is applied, preserving the behaviour of definitions authored
    /// before verb declaration existed. Well-known values are defined in <see cref="FunctionVerb"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> Verbs => verbs.AsReadOnly();

    /// <summary>
    /// Optional <c>sys-schemas</c> contract describing this function's request body. When set, the
    /// runtime validates the request body against the winning entry before executing any task.
    /// Authored either as a single component reference or as rule-based entries evaluated in
    /// declaration order (first match wins, a trailing rule-less entry is the fallback).
    /// </summary>
    [JsonInclude] [JsonPropertyName("inputSchema")]
    [JsonConverter(typeof(SchemaSelectionJsonConverter))]
    public SchemaSelection?InputSchema { get; private set; }

    /// <summary>
    /// Optional <c>sys-schemas</c> contract describing this function's response body.
    /// Declarative only - the runtime does not validate responses against it.
    /// </summary>
    [JsonInclude] [JsonPropertyName("outputSchema")]
    [JsonConverter(typeof(SchemaSelectionJsonConverter))]
    public SchemaSelection?OutputSchema { get; private set; }

    /// <summary>
    /// Optional <c>sys-views</c> contract the client renders to collect this function's input.
    /// Supports the same single-reference and rule-based forms as <see cref="InputSchema"/>.
    /// </summary>
    [JsonInclude] [JsonPropertyName("inputView")]
    [JsonConverter(typeof(ViewDefinitionJsonConverter))]
    public ViewDefinition? InputView { get; private set; }

    /// <summary>
    /// Optional <c>sys-views</c> contract the client renders to present this function's output.
    /// </summary>
    [JsonInclude] [JsonPropertyName("outputView")]
    [JsonConverter(typeof(ViewDefinitionJsonConverter))]
    public ViewDefinition? OutputView { get; private set; }

    /// <summary>
    /// True when the function declares at least one input schema entry. A definition whose entries all
    /// carry rules can still resolve to no schema at request time; this only reports the declaration.
    /// </summary>
    [JsonIgnore]
    public bool HasInputSchema => InputSchema is { Schemas.Count: > 0 };

    /// <summary>True when the function declares at least one output schema entry.</summary>
    [JsonIgnore]
    public bool HasOutputSchema => OutputSchema is { Schemas.Count: > 0 };

    /// <summary>True when the function declares at least one input view entry.</summary>
    [JsonIgnore]
    public bool HasInputView => InputView is { Views.Count: > 0 };

    /// <summary>True when the function declares at least one output view entry.</summary>
    [JsonIgnore]
    public bool HasOutputView => OutputView is { Views.Count: > 0 };

    [JsonInclude] [JsonPropertyName("roles")]
    private List<RoleGrant> roles = new();

    /// <summary>
    /// Function roles for authorization (domain-qualified). DENY always overrides ALLOW.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> Roles => roles.AsReadOnly();

    public static string ComponentTypeKey => RuntimeSysSchemaInfo.Functions;
    public string ComponentKey => ComponentTypeKey;

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), FunctionConstants.MaxKeyLength);
    }

    private void SetDomain(string domain)
    {
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
    }

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    public List<OnExecuteTask> GetExecuteTasks() =>
        onExecutionTasks.Count > 0 ? onExecutionTasks : [Task!];

    /// <summary>
    /// True when this function accepts the given HTTP verb. A function that declares no verbs
    /// accepts every verb, so existing definitions keep their current behaviour.
    /// </summary>
    /// <param name="httpMethod">The incoming HTTP method. A null or blank value is treated as unrestricted.</param>
    public bool SupportsVerb(string? httpMethod)
    {
        if (verbs.Count == 0 || string.IsNullOrWhiteSpace(httpMethod))
            return true;

        var normalized = FunctionVerb.Normalize(httpMethod);
        return verbs.Contains(normalized, StringComparer.Ordinal);
    }

    /// <summary>
    /// Trims, upper-cases and de-duplicates authored verbs so comparison and the <c>Allow</c> header
    /// are stable regardless of how the component JSON was written.
    /// </summary>
    private static List<string> NormalizeVerbs(List<string>? authored)
    {
        if (authored is null || authored.Count == 0)
            return [];

        return authored
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(FunctionVerb.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public void SetReference(IReference reference)
    {
        SetKey(reference.Key);
        SetDomain(reference.Domain);
        SetVersion(reference.Version);
    }
}