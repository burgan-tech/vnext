using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// It is in the possible statuses found in the flow.
/// </summary>
public sealed class State : IHasKey
{
    private State()
    {
    }

    private State(
        string key,
        StateType stateType,
        StateSubType subType)
    {
        SetKey(key);
        StateType = stateType;
        SubType = subType;
    
        labels = [];
        onEntries = [];
        onExits = [];
    }

    [JsonConstructor]
    private State(
        string key,
        StateType stateType,
        StateSubType subType,
        VersionStrategy versionStrategy,
        List<LanguageLabel>? labels,
        List<OnExecuteTask>? onEntries,
        List<OnExecuteTask>? onExits,
        ViewDefinition view,
        ErrorBoundary errorBoundary)
        : this(key, stateType, subType)
    {
        VersionStrategy = versionStrategy;
        this.labels = labels ?? [];
        this.onEntries = onEntries ?? [];
        this.onExits = onExits ?? [];
        this.view = view;
        this.errorBoundary = errorBoundary;
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// Version strategy
    /// </summary>
    public VersionStrategy VersionStrategy { get; private set; }

    /// <summary>
    /// <see cref="StateType"/>
    /// </summary>
    public StateType StateType { get; private set; }

    /// <summary>
    /// <see cref="SubType"/>
    /// </summary>
    public StateSubType SubType { get; private set; }

    [JsonInclude] [JsonPropertyName("labels")]
    private List<LanguageLabel> labels = new();

    [JsonInclude] [JsonPropertyName("transitions")]
    private List<Transition> transitions = new();

    [JsonInclude] [JsonPropertyName("onEntries")]
    private List<OnExecuteTask> onEntries = new();

    [JsonInclude] [JsonPropertyName("onExits")]
    private List<OnExecuteTask> onExits = new();

    [JsonInclude] [JsonPropertyName("subFlow")]
    public SubFlow? SubFlow { get; private set; }
    [JsonInclude] [JsonPropertyName("view")]
    public ViewDefinition? view  { get; private set; }

    [JsonInclude] [JsonPropertyName("errorBoundary")]
    private ErrorBoundary? errorBoundary;

    /// <summary>
    /// Languages
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<LanguageLabel> Labels => labels.AsReadOnly();

    /// <summary>
    /// State view
    /// </summary>
    [JsonIgnore]
    public ViewDefinition? View => view;

    /// <summary>
    /// Error boundary for this state.
    /// Overrides workflow-level error handling for tasks executed in this state.
    /// </summary>
    [JsonIgnore]
    public ErrorBoundary? ErrorBoundary => errorBoundary;

    /// <summary>
    /// Transitions
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<Transition> Transitions => transitions.AsReadOnly();

    /// <summary>
    /// On entries
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<OnExecuteTask> OnEntries => onEntries.AsReadOnly();

    /// <summary>
    /// On exits
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<OnExecuteTask> OnExits => onExits.AsReadOnly();

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), StateConstants.MaxKeyLength);
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

    public void AddOnEntry(OnExecuteTask task)
    {
        onEntries.Add(task);
    }

    public void AddOnExit(OnExecuteTask task)
    {
        onExits.Add(task);
    }

    public void SetView(ViewDefinition viewDefinition)
    {
        view = viewDefinition;
    }

    /// <summary>
    /// Sets the error boundary for this state.
    /// </summary>
    /// <param name="boundary">The error boundary configuration.</param>
    public void SetErrorBoundary(ErrorBoundary boundary)
    {
        errorBoundary = boundary;
    }
    
    /// <summary>
    /// Sets the SubFlow configuration for this state.
    /// </summary>
    /// <param name="type">The SubFlow type.</param>
    /// <param name="reference">Reference to the child workflow.</param>
    /// <param name="mapping">Data mapping script.</param>
    /// <param name="viewOverrides">Optional view overrides.</param>
    /// <param name="errorBoundary">Optional error boundary for SubFlow execution errors.</param>
    /// <param name="errorPolicy">Optional error propagation policy to parent workflow.</param>
    public void SetSubFlow(
        string type,
        IReference reference,
        ScriptCode mapping,
        Dictionary<string, Reference>? viewOverrides,
        ErrorBoundary? errorBoundary = null,
        SubFlowErrorPolicy? errorPolicy = null)
    {
        SubFlow = SubFlow.Create(type, reference, mapping, viewOverrides, errorBoundary, errorPolicy);
    }

    public void AddTransition(Transition transition)
    {
        transitions.Add(transition);
    }

    public Transition? FindTransition(string key)
    {
        return Transitions.FirstOrDefault(t => t.Key == key);
    }

    public IEnumerable<Transition> AutoTransitions => Transitions.Where(p => p.TriggerType == TriggerType.Automatic);
    
    public IEnumerable<Transition> ScheduledTransitions => Transitions.Where(p => p.TriggerType == TriggerType.Scheduled);

    public IReadOnlyList<string> TransitionKeys() => Transitions.Select(t => t.Key).ToList();

    public static State Create(
        string key,
        StateType stateType,
        StateSubType stateSubType,
        string versionStrategy
    )
    {
        return new State(
            key,
            stateType,
            stateSubType)
        {
            VersionStrategy = VersionStrategy.FromCode(versionStrategy)
        };
    }
}