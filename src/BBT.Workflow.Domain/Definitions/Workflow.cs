using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Workflow.Domain;
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
        List<LanguageLabel> labels,
        List<IReference> functions,
        List<IReference> features,
        List<State> states,
        List<Transition> sharedTransitions,
        List<Reference> extensions,
        Transition startTransition
    ) : this()
    {
        Type = type;
        Timeout = timeout;
        this.labels = labels;
        this.functions = functions;
        this.features = features;
        this.states = states ;
        this.extensions = extensions;
        this.sharedTransitions = sharedTransitions;
        StartTransition = startTransition;
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
    public string SemanticVersion => Version.Contains('+') ? Regex.Match(Version, @"^([^+]+)").Groups[1].Value : Version;

    public string CacheKey => $"{nameof(Workflow)}:{Domain}:{Flow}:{Key}:{Version}";

    public static string GenerateCacheKey(
        string domain,
        string flow,
        string key,
        string version)
    {
        return $"{nameof(Workflow)}:{domain}:{flow}:{key}:{version}";
    }

    /// <summary>
    /// When the workflow starts, a timer counts down.
    /// If the workflow is not completed within this time,
    /// it is automatically pulled to the targeted status.
    /// </summary>
    public WorkflowTimeout? Timeout { get; private set; }

    [JsonInclude] [JsonPropertyName("labels")]
    private List<LanguageLabel> labels = new();

    [JsonInclude] [JsonPropertyName("functions")]
    private List<IReference> functions = new();

    [JsonInclude] [JsonPropertyName("features")]
    private List<IReference> features = new();

    [JsonInclude] [JsonPropertyName("sharedTransitions")]
    private List<Transition> sharedTransitions = new();

    [JsonInclude] [JsonPropertyName("extensions")]
    private List<Reference> extensions = new();

    [JsonInclude] [JsonPropertyName("states")]
    private List<State> states = new();
      /// <summary>
    /// Master schema for the complete instance data model (from Issue #110)
    /// </summary>
    [JsonInclude] 
    public Reference? Schema { get; private set; }
    
    /// <summary>
    /// Instance data validation configuration
    /// </summary>
    [JsonInclude] 
    public InstanceDataValidationConfig? InstanceDataValidation { get; private set; }

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

    public void AddFunction(IReference reference)
    {
        functions.Add(reference);
    }

    public void AddFeature(IReference reference)
    {
        features.Add(reference);
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

    /// <summary>
    /// Sets the master schema reference for instance data validation
    /// </summary>
    /// <param name="schema">The schema reference to use for validation</param>
    public void SetSchema(IReference? schema)
    {
        Schema = schema?.ToReference();
    }

    /// <summary>
    /// Sets the instance data validation configuration
    /// </summary>
    /// <param name="config">The validation configuration</param>
    public void SetInstanceDataValidation(InstanceDataValidationConfig? config)
    {
        InstanceDataValidation = config;
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
               ?? (StartTransition.Key == key ? StartTransition : null);
    }

    public Transition? ResolveTransition(string key, State currentState)
    {
        return currentState.FindTransition(key) ?? FindTransition(key);
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
        
        // Get manual shared transitions
        var manualSharedTransitions = SharedTransitions
            .Where(t => t.AvailableIn.Contains(currentState.Key) && 
                        (t.TriggerType == TriggerType.Manual || t.TriggerType == TriggerType.Event))
            .Select(t => t.Key)
            .ToList();
        
        manualTransitions.AddRange(manualSharedTransitions);
        return manualTransitions;
    }

    public static Workflow Create()
    {
        return new Workflow();
    }

    
}
public sealed class InstanceDataValidationConfig
{
    /// <summary>
    /// When to validate instance data against the master schema
    /// </summary>
    public InstanceDataValidationMode ValidationMode { get; set; } = InstanceDataValidationMode.PostExecution;
}
public enum InstanceDataValidationMode
{
    /// <summary>
    /// No validation
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Validate after task execution
    /// </summary>
    PostExecution = 1,
    
    /// <summary>
    /// Validate on every data change
    /// </summary>
    OnDataChange = 2
}