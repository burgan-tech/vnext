using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Workflow definition
/// </summary>
public sealed class Workflow : IDomainEntity, IReference, IReferenceSetter, IHasCreatedAt
{
    private Workflow()
    {
        Flow = RuntimeSysSchemaInfo.Flows;
        labels = [];
        functions = [];
        features = [];
        states = [];
        extensions = [];
        sharedTransitions = [];
    }

    [JsonConstructor]
    private Workflow(
        WorkflowType type,
        WorkflowTimeout timeout,
        Transition cancel,
        Transition updateData,
        Transition exit,
        List<LanguageLabel> labels,
        List<Reference> functions,
        List<Reference> features,
        List<State> states,
        List<Transition> sharedTransitions,
        List<Reference> extensions,
        Transition startTransition,
        List<RoleGrant>? queryRoles = null
    ) : this()
    {
        Type = type;
        Timeout = timeout;
        Cancel = cancel;
        UpdateData = updateData;
        Exit = exit;
        this.labels = labels;
        this.functions = functions ?? [];
        this.features = features ?? [];
        this.states = states;
        this.extensions = extensions ?? [];
        this.sharedTransitions = sharedTransitions ?? [];
        StartTransition = startTransition;
        this.queryRoles = queryRoles ?? [];
    }

    /// <summary>
    /// It is the key value for the heat flow.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// Information about which domain the flow is working on and which domain it belongs to.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; init; }

    /// <summary>
    /// Semantic version number. There may be more than one version on the runtime.
    /// </summary>
    public string Version { get; private set; } = "1.0.0";

    /// <summary>
    /// Determines the course of the flow.
    /// </summary>
    public WorkflowType Type { get; private set; }

    public bool IsSub => Type.Equals(WorkflowType.SubFlow) || Type.Equals(WorkflowType.SubProcess);

    /// <summary>
    /// Created at
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Semantic version
    /// </summary>
    public string SemanticVersion =>
        Version.Contains('+') ? Regex.Match(Version, @"^([^+]+)").Groups[1].Value : Version;

    public string ComponentKey => RuntimeSysSchemaInfo.Flows;
    
    /// <summary>
    /// When the workflow starts, a timer counts down.
    /// If the workflow is not completed within this time,
    /// it is automatically pulled to the targeted status.
    /// </summary>
    public WorkflowTimeout? Timeout { get; private set; }

    /// <summary>
    /// Defines the cancellation configuration for this workflow.
    /// When configured, allows the workflow to be canceled via the cancel transition.
    /// </summary>
    [JsonInclude] [JsonPropertyName("cancel")]
    public Transition? Cancel { get; private set; }

    /// <summary>
    /// Defines the update data configuration for this workflow.
    /// When configured, allows the workflow to update data via the updateData transition.
    /// </summary>
    [JsonInclude] [JsonPropertyName("updateData")]
    public Transition? UpdateData { get; private set; }

    /// <summary>
    /// Defines the exit configuration for this workflow.
    /// When configured, allows the workflow to be exited via the exit transition.
    /// </summary>
    [JsonInclude] [JsonPropertyName("exit")]
    public Transition? Exit { get; private set; }

    /// <summary>
    /// Global error boundary for the workflow.
    /// Applied when no task or state-level boundary handles the error.
    /// </summary>
    [JsonInclude] [JsonPropertyName("errorBoundary")]
    public ErrorBoundary? ErrorBoundary { get; private set; }


    [JsonInclude] [JsonPropertyName("labels")]
    private List<LanguageLabel> labels = new();

    [JsonInclude] [JsonPropertyName("functions")]
    private List<Reference> functions = new();

    [JsonInclude] [JsonPropertyName("features")]
    private List<Reference> features = new();

    [JsonInclude] [JsonPropertyName("sharedTransitions")]
    private List<Transition> sharedTransitions = new();

    [JsonInclude] [JsonPropertyName("extensions")]
    private List<Reference> extensions = new();

    [JsonInclude] [JsonPropertyName("states")]
    private List<State> states = new();

    [JsonInclude] [JsonPropertyName("queryRoles")]
    private List<RoleGrant> queryRoles = new();

    [JsonInclude] [JsonPropertyName("schema")] 
    public Reference? Schema { get; private set; }

    /// <summary>
    /// Root-level query roles for state-based visibility. When a state has no queryRoles, this is used.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> QueryRoles => queryRoles.AsReadOnly();

    /// <summary>
    /// It is a content set with multiple language options for the content to be displayed to the user.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<LanguageLabel> Labels => labels.AsReadOnly();

    /// <summary>
    /// These are function definitions that will be distributed with the flow.
    /// In general, BFF and calculation methods are defined as functions.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<IReference> Functions => functions.AsReadOnly();

    /// <summary>
    /// Definitions that include transition and interface components
    /// that can be used in common in all flows such as adding documents and adding notes.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<IReference> Features => features.AsReadOnly();

    /// <summary>
    /// It is used for common transition definitions such as Cancel in the flow.
    /// It is to prevent redefinition in each state that passes.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<Transition> SharedTransitions => sharedTransitions.AsReadOnly();

    /// <summary>
    /// Specifies additional functions to be run when a recording of a flow sample is requested.
    /// It is generally used to enrich the recording.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<IReference> Extensions => extensions.AsReadOnly();

    /// <summary>
    /// All flows are started with a fixed transition named start.
    /// There is no interface component in the transition but it can receive a dataset.
    /// It contains the basic definitions related to this transition.
    /// </summary>
    public Transition StartTransition { get; private set; }

    /// <summary>
    /// It is in the possible statuses found in the flow.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<State> States => states.AsReadOnly();

    #region Private methods

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), WorkflowConstants.MaxKeyLength);
    }

    private void SetDomain(string domain)
    {
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
    }

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    #endregion

    public void SetReference(IReference reference)
    {
        SetKey(reference.Key);
        SetDomain(reference.Domain);
        SetVersion(reference.Version);
    }

    public void SetType(string type)
    {
        Type = WorkflowType.FromCode(type);
    }

    public void AddLanguage(string label, string language)
    {
        if (labels.All(l => l.Language != language))
        {
            labels.Add(new LanguageLabel(label, language));
        }
        else
        {
            var languageLabel = labels.First(p => p.Language == language);
            labels.Remove(languageLabel);
            labels.Add(new LanguageLabel(label, language));
        }
    }

    public void SetTimeout(WorkflowTimeout timeout)
    {
        Timeout = timeout;
    }

    public void SetCancel(Transition cancel)
    {
        Cancel = cancel;
    }

    public void SetUpdateData(Transition updateData)
    {
        UpdateData = updateData;
    }

    public void SetExit(Transition exit)
    {
        Exit = exit;
    }

    public void SetErrorBoundary(ErrorBoundary errorBoundary)
    {
        ErrorBoundary = errorBoundary;
    }

    public void AddFunction(IReference reference)
    {
        functions.Add(reference.ToReference());
    }

    public void AddFeature(IReference reference)
    {
        features.Add(reference.ToReference());
    }

    public void AddExtension(IReference reference)
    {
        extensions.Add(reference.ToReference());
    }

    public void AddSharedTransition(Transition transition)
    {
        sharedTransitions.Add(transition);
    }

    public void SetStartTransition(Transition transition)
    {
        StartTransition = transition;
    }

    public void AddState(State state)
    {
        states.Add(state);
    }

    public Result<State> GetInitialState()
    {
        var state = States.FirstOrDefault(s => s.StateType == StateType.Initial);
        return state is not null
            ? Result<State>.Ok(state)
            : Result<State>.Fail(WorkflowErrors.StateNotFound(Key, "initial"));
    }

    public Result<State> GetState(string key)
    {
        var state = States.FirstOrDefault(s => s.Key == key);
        return state is not null
            ? Result<State>.Ok(state)
            : Result<State>.Fail(WorkflowErrors.StateNotFound(Key, key));
    }

    /// <summary>
    /// Gets a state by key, resolving well-known state keys (like $self) to actual states.
    /// </summary>
    /// <param name="key">The state key to retrieve</param>
    /// <param name="currentStateKey">The current state key, used to resolve $self references</param>
    /// <returns>Result containing the resolved state or an error if not found</returns>
    public Result<State> GetState(string key, string currentStateKey)
    {
        // Domain Logic: Resolve well-known state keys
        var resolvedKey = WellKnownStateKeys.ReservedTargetKeys.Contains(key)
            ? currentStateKey  // $self resolves to current state
            : key;

        return GetState(resolvedKey);
    }

    public State? FindState(string key)
    {
        return States.FirstOrDefault(s => s.Key == key);
    }

    public Transition? FindSharedTransition(string key)
    {
        return SharedTransitions.FirstOrDefault(t => t.Key == key);
    }

    public Transition? FindTransition(string key)
    {
        return FindSharedTransition(key)
               ?? (StartTransition.Key == key ? StartTransition : null)
               ?? (Cancel?.Key == key ? Cancel : null)
               ?? (UpdateData?.Key == key ? UpdateData : null)
               ?? (Exit?.Key == key ? Exit : null);
    }

    public Transition? ResolveTransition(string key, State currentState)
    {
        var requestedKey = ResolveWellKnownKey(key);
        return currentState.FindTransition(requestedKey) ?? FindTransition(requestedKey);
    }

    /// <summary>
    /// Resolves well-known transition keys to their configured transition keys.
    /// </summary>
    /// <param name="requestedKey">The requested transition key</param>
    /// <returns>The resolved transition key</returns>
    /// <exception cref="BusinessException">Thrown when a well-known key is requested but not configured</exception>
    private string ResolveWellKnownKey(string requestedKey)
    {
        if (string.Equals(requestedKey, WellKnownTransitionKeys.Cancel, StringComparison.OrdinalIgnoreCase))
        {
            // If this flow does not have cancel configuration, "cancel" is not supported
            if (Cancel is null)
                throw new CancelNotConfiguredForWorkflowException(Key);

            return Cancel.Key;
        }

        if (string.Equals(requestedKey, WellKnownTransitionKeys.UpdateData, StringComparison.OrdinalIgnoreCase))
        {
            // If this flow does not have updateData configuration, "updateData" is not supported
            if (UpdateData is null)
                throw new UpdateDataNotConfiguredForWorkflowException(Key);

            return UpdateData.Key;
        }

        if (string.Equals(requestedKey, WellKnownTransitionKeys.Exit, StringComparison.OrdinalIgnoreCase))
        {
            // If this flow does not have exit configuration, "exit" is not supported
            if (Exit is null)
                throw new ExitNotConfiguredForWorkflowException(Key);

            return Exit.Key;
        }

        return requestedKey;
    }

    public Transition? FindTransitionInContext(string key)
    {
        return FindTransition(key)
               ?? States.SelectMany(s => s.Transitions).FirstOrDefault(p => p.Key == key);
    }

    public List<string> GetSharedTransitionKeys(string state)
    {
        return SharedTransitions.Where(s => s.Key == state).Select(s => s.Key).ToList();
    }

    public List<string> GetAvailableUserTransitionKeys(State currentState)
    {
        // Get manual transitions from current state
        var manualTransitions = currentState.Transitions
            .Where(t => t.TriggerType == TriggerType.Manual || t.TriggerType == TriggerType.Event)
            .Select(t => t.Key)
            .ToList();

        // Get manual shared transitions (AvailableIn empty/null = available in all states, aligned with SharedTransitionAvailabilitySpecification)
        var manualSharedTransitions = GetAvailableSharedTransitionKeysOnly(currentState);
        manualTransitions.AddRange(manualSharedTransitions);
        return manualTransitions;
    }

    /// <summary>
    /// Gets only the manual/event shared transition keys available in the given state.
    /// Does not include state-level transitions. Used when instance is in subflow to expose parent shared transitions only.
    /// </summary>
    public List<string> GetAvailableSharedTransitionKeysOnly(State currentState)
    {
        return SharedTransitions
            .Where(t => (t.AvailableIn == null || !t.AvailableIn.Any() || t.AvailableIn.Contains(currentState.Key)) &&
                        (t.TriggerType == TriggerType.Manual || t.TriggerType == TriggerType.Event))
            .Select(t => t.Key)
            .ToList();
    }

    public static Workflow Create()
    {
        return new Workflow();
    }
}